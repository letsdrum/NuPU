using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Spectre.Console;
using Spectre.Console.Cli;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace NuPU
{
    internal class UpdateCommand : AsyncCommand<UpdateCommand.UpdateCommandSettings>
    {
        private const string UpToDate = " [green]up to date[/]";
        private const string NeedsUpdate = " [red]needs update[/]";
        private readonly List<string> _mergeRequests = new();

        public override async Task<int> ExecuteAsync(CommandContext context, UpdateCommandSettings updateCommandSettings)
        {
            if (updateCommandSettings.Version)
            {
                var version = GetType().Assembly.GetName().Version;
                if (version != null) AnsiConsole.WriteLine(version.ToString());
                return 0;
            }

            var rootPath = string.IsNullOrWhiteSpace(updateCommandSettings.Directory) || !Directory.Exists(updateCommandSettings.Directory)
                ? Directory.GetCurrentDirectory()
                : updateCommandSettings.Directory;

            var rootDir = new DirectoryInfo(rootPath);
            var csProjFiles = rootDir.EnumerateFiles("*.csproj", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = updateCommandSettings.Recursive });
            var ignoreDirs = new[] { ".git", ".github", ".vs", ".vscode", "bin", "obj", "packages", "node_modules" };
            foreach (var csProjFile in csProjFiles.Where(f => !ignoreDirs.Contains(f.DirectoryName)))
            {
                var settings = Settings.LoadDefaultSettings(csProjFile.Directory.FullName);
                var enabledSources = SettingsUtility.GetEnabledSources(settings);
                AnsiConsole.MarkupLine($"Analyzing [yellow]{csProjFile.FullName}[/]");
                var packages = new List<Package>();
                using (var fileStream = File.OpenRead(csProjFile.FullName))
                {
                    try
                    {
                        var document = XDocument.Load(fileStream);
                        var ns = document.Root.GetDefaultNamespace();
                        var project = document.Element(ns + "Project");
                        var itemGroups = project
                            .Elements(ns + "ItemGroup")
                            .ToList();
                        packages.AddRange(itemGroups.SelectMany(ig => ig.Elements(ns + "PackageReference")).Select(e => new Package
                        {
                            Id = e.Attribute("Include")?.Value,
                            Version = e.Attribute("Version")?.Value ?? e.Element(ns + "Version")?.Value,
                        }));
                    }
                    catch (XmlException e)
                    {
                        AnsiConsole.MarkupLine($"[red]Error[/] {e.Message}");
                        continue;
                    }
                }

                if (packages.Count() == 0) continue;

                foreach (var package in packages.Where(p => string.IsNullOrWhiteSpace(updateCommandSettings.Package) || string.Equals(p.Id, updateCommandSettings.Package, StringComparison.OrdinalIgnoreCase)))
                {
                    NuGetVersion nugetVersion = null;
                    if (VersionRange.TryParse(package.Version, out VersionRange versionRange))
                    {
                        nugetVersion = versionRange.MinVersion;
                    }
                    else if (NuGetVersion.TryParse(package.Version, out NuGetVersion parsedVersion))
                    {
                        nugetVersion = parsedVersion;
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[yellow]Skipping {package.Id} because of unknown version[/]");
                        continue;
                    }

                    AnsiConsole.Markup(package.Id);

                    var showUpToDate = true;
                    foreach (var source in enabledSources)
                    {
                        var repository = new SourceRepository(source, Repository.Provider.GetCoreV3());
                        try
                        {
                            FindPackageByIdResource resource = await repository.GetResourceAsync<FindPackageByIdResource>();
                            using var cacheContext = new SourceCacheContext();
                            var allVersions = await resource.GetAllVersionsAsync(package.Id, cacheContext, NullLogger.Instance, CancellationToken.None);
                            var newerVersions = allVersions.Where(v => v > nugetVersion).ToList();
                            if (!newerVersions.Any())
                            {
                                continue;
                            }

                            var stableVersions = newerVersions.Where(v => !v.IsPrerelease);

                            var versionsToShow = new List<NuGetVersion>();
                            versionsToShow.AddRange(HighestMajor(stableVersions, nugetVersion));
                            versionsToShow.AddRange(HighestMinor(stableVersions, nugetVersion));
                            versionsToShow.AddRange(HighestPatch(stableVersions, nugetVersion));
                            versionsToShow.AddRange(HighestRevision(stableVersions, nugetVersion));

                            if (updateCommandSettings.IncludePrerelease)
                            {
                                var prereleaseVersions = newerVersions.Where(v => v.IsPrerelease);
                                versionsToShow.AddRange(HighestMajor(prereleaseVersions, nugetVersion));
                                versionsToShow.AddRange(HighestMinor(prereleaseVersions, nugetVersion));
                                versionsToShow.AddRange(HighestPatch(prereleaseVersions, nugetVersion));
                                versionsToShow.AddRange(HighestRevision(prereleaseVersions, nugetVersion));
                            }

                            if (versionsToShow.Count == 0)
                            {
                                continue;
                            }

                            var choices = new List<string>();
                            var currentVersionString = $"{nugetVersion.OriginalVersion} (current)";
                            choices.Add(currentVersionString);
                            choices.AddRange(versionsToShow.OrderBy(v => v).Select(v => v.ToString()));

                            showUpToDate = false;
                            AnsiConsole.MarkupLine(NeedsUpdate);
                            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>().PageSize(10).AddChoices(choices.ToArray()));

                            if (choice == currentVersionString) continue;

                            if (!string.IsNullOrEmpty(updateCommandSettings.SourceBranch))
                            {
                                var gitCheckoutSource = new ProcessStartInfo("git", $"checkout {updateCommandSettings.SourceBranch}")
                                {
                                    WorkingDirectory = csProjFile.Directory.FullName,
                                    CreateNoWindow = true,
                                    UseShellExecute = false,
                                    RedirectStandardOutput = true,
                                    RedirectStandardError = true,
                                };
                                
                                var gitCheckoutSourceProcess = Process.Start(gitCheckoutSource);
                                var gitCheckoutSourceOutputAndError = await Task.WhenAll(
                                    gitCheckoutSourceProcess.StandardOutput.ReadToEndAsync(), 
                                    gitCheckoutSourceProcess.StandardError.ReadToEndAsync());
                                
                                gitCheckoutSourceProcess.WaitForExit();
                                
                                var gitCheckoutSourceExitCode = gitCheckoutSourceProcess.ExitCode;

                                if (gitCheckoutSourceExitCode != 0)
                                {
                                    if (!string.IsNullOrWhiteSpace(gitCheckoutSourceOutputAndError[0])) Console.WriteLine(gitCheckoutSourceOutputAndError[0]);
                                    if (!string.IsNullOrWhiteSpace(gitCheckoutSourceOutputAndError[1])) AnsiConsole.MarkupLine($"[red]{gitCheckoutSourceOutputAndError[1]}[/]");
                                    return -1;
                                }

                                if (!string.IsNullOrWhiteSpace(gitCheckoutSourceOutputAndError[0]))
                                {
                                    var lines = gitCheckoutSourceOutputAndError[0].Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                                    if (lines.Length > 0 && lines.Last().StartsWith("error"))
                                    {
                                        AnsiConsole.MarkupLine($"[red]{lines.Last()}[/]");
                                    }
                                }
                            }
                            
                            if (!string.IsNullOrEmpty(updateCommandSettings.TargetBranch))
                            {
                                var gitCheckoutTarget = new ProcessStartInfo("git", $"checkout -b {updateCommandSettings.TargetBranch}")
                                {
                                    WorkingDirectory = csProjFile.Directory.FullName,
                                    CreateNoWindow = true,
                                    UseShellExecute = false,
                                    RedirectStandardOutput = true,
                                    RedirectStandardError = true,
                                };
                                
                                var gitCheckoutTargetProcess = Process.Start(gitCheckoutTarget);
                                var gitCheckoutTargetOutputAndError = await Task.WhenAll(
                                    gitCheckoutTargetProcess.StandardOutput.ReadToEndAsync(), 
                                    gitCheckoutTargetProcess.StandardError.ReadToEndAsync());
                                
                                await gitCheckoutTargetProcess.WaitForExitAsync();
                                
                                var gitCheckoutTargetExitCode = gitCheckoutTargetProcess.ExitCode;

                                if (gitCheckoutTargetExitCode != 0)
                                {
                                    if (!string.IsNullOrWhiteSpace(gitCheckoutTargetOutputAndError[0])) Console.WriteLine(gitCheckoutTargetOutputAndError[0]);
                                    if (!string.IsNullOrWhiteSpace(gitCheckoutTargetOutputAndError[1])) AnsiConsole.MarkupLine($"[red]{gitCheckoutTargetOutputAndError[1]}[/]");
                                    return -1;
                                }

                                if (!string.IsNullOrWhiteSpace(gitCheckoutTargetOutputAndError[0]))
                                {
                                    var lines = gitCheckoutTargetOutputAndError[0].Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                                    if (lines.Length > 0 && lines.Last().StartsWith("error"))
                                    {
                                        AnsiConsole.MarkupLine($"[red]{lines.Last()}[/]");
                                    }
                                }
                            }

                            var dotnet = new ProcessStartInfo("dotnet", $"add package {package.Id} -v {choice} -s {source.SourceUri}")
                            {
                                WorkingDirectory = csProjFile.Directory.FullName,
                                CreateNoWindow = true,
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                            };
                            var process = Process.Start(dotnet);
                            var outputAndError = await Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());

                            await process.WaitForExitAsync();
                            var exitCode = process.ExitCode;

                            if (exitCode != 0)
                            {
                                if (!string.IsNullOrWhiteSpace(outputAndError[0])) Console.WriteLine(outputAndError[0]);
                                if (!string.IsNullOrWhiteSpace(outputAndError[1])) AnsiConsole.MarkupLine($"[red]{outputAndError[1]}[/]");
                                return -1;
                            }

                            if (!string.IsNullOrWhiteSpace(outputAndError[0]))
                            {
                                var lines = outputAndError[0].Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                                if (lines.Length > 0 && lines.Last().StartsWith("error"))
                                {
                                    AnsiConsole.MarkupLine($"[red]{lines.Last()}[/]");
                                }
                            }
                            
                            if (!string.IsNullOrEmpty(updateCommandSettings.Commit))
                            {
                                var gitAdd = new ProcessStartInfo("git", $"add --all")
                                {
                                    WorkingDirectory = csProjFile.Directory.FullName,
                                    CreateNoWindow = true,
                                    UseShellExecute = false,
                                    RedirectStandardOutput = true,
                                    RedirectStandardError = true,
                                };
                                
                                var gitAddProcess = Process.Start(gitAdd);
                                var gitAddOutputAndError = await Task.WhenAll(
                                    gitAddProcess.StandardOutput.ReadToEndAsync(), 
                                    gitAddProcess.StandardError.ReadToEndAsync());
                                
                                await gitAddProcess.WaitForExitAsync();
                                
                                var gitAddExitCode = gitAddProcess.ExitCode;

                                if (gitAddExitCode != 0)
                                {
                                    if (!string.IsNullOrWhiteSpace(gitAddOutputAndError[0])) Console.WriteLine(gitAddOutputAndError[0]);
                                    if (!string.IsNullOrWhiteSpace(gitAddOutputAndError[1])) AnsiConsole.MarkupLine($"[red]{gitAddOutputAndError[1]}[/]");
                                    return -1;
                                }

                                if (!string.IsNullOrWhiteSpace(gitAddOutputAndError[0]))
                                {
                                    var lines = gitAddOutputAndError[0].Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                                    if (lines.Length > 0 && lines.Last().StartsWith("error"))
                                    {
                                        AnsiConsole.MarkupLine($"[red]{lines.Last()}[/]");
                                    }
                                }
                                
                                var gitCommit = new ProcessStartInfo("git", $"commit -m \"{updateCommandSettings.Commit}\"")
                                {
                                    WorkingDirectory = csProjFile.Directory.FullName,
                                    CreateNoWindow = true,
                                    UseShellExecute = false,
                                    RedirectStandardOutput = true,
                                    RedirectStandardError = true,
                                };
                                
                                var gitCommitProcess = Process.Start(gitCommit);
                                var gitCommitOutputAndError = await Task.WhenAll(
                                    gitCommitProcess.StandardOutput.ReadToEndAsync(), 
                                    gitCommitProcess.StandardError.ReadToEndAsync());
                                
                                await gitCommitProcess.WaitForExitAsync();
                                
                                var gitCommitExitCode = gitCommitProcess.ExitCode;

                                if (gitCommitExitCode != 0)
                                {
                                    if (!string.IsNullOrWhiteSpace(gitCommitOutputAndError[0])) Console.WriteLine(gitCommitOutputAndError[0]);
                                    if (!string.IsNullOrWhiteSpace(gitCommitOutputAndError[1])) AnsiConsole.MarkupLine($"[red]{gitCommitOutputAndError[1]}[/]");
                                    return -1;
                                }

                                if (!string.IsNullOrWhiteSpace(gitCommitOutputAndError[0]))
                                {
                                    var lines = gitCommitOutputAndError[0].Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                                    if (lines.Length > 0 && lines.Last().StartsWith("error"))
                                    {
                                        AnsiConsole.MarkupLine($"[red]{lines.Last()}[/]");
                                    }
                                }
                            }
                            
                            if (updateCommandSettings.PushTargetBranch)
                            {
                                var gitPush = new ProcessStartInfo("git", $"push origin {updateCommandSettings.TargetBranch}")
                                {
                                    WorkingDirectory = csProjFile.Directory.FullName,
                                    CreateNoWindow = true,
                                    UseShellExecute = false,
                                    RedirectStandardOutput = true,
                                    RedirectStandardError = true,
                                };
                                
                                var gitPushProcess = Process.Start(gitPush);
                                var gitPushOutputAndError = await Task.WhenAll(
                                    gitPushProcess.StandardOutput.ReadToEndAsync(), 
                                    gitPushProcess.StandardError.ReadToEndAsync());
                                
                                await gitPushProcess.WaitForExitAsync();
                                
                                var gitPushExitCode = gitPushProcess.ExitCode;

                                if (gitPushExitCode != 0)
                                {
                                    if (!string.IsNullOrWhiteSpace(gitPushOutputAndError[0])) Console.WriteLine(gitPushOutputAndError[0]);
                                    if (!string.IsNullOrWhiteSpace(gitPushOutputAndError[1])) AnsiConsole.MarkupLine($"[red]{gitPushOutputAndError[1]}[/]");
                                    return -1;
                                }

                                var line = gitPushOutputAndError
                                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
                                    .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                                    .FirstOrDefault();
                                
                                var regex = new Regex(@"(http|ftp|https):\/\/([\w_-]+(?:(?:\.[\w_-]+)+))([\w.,@?^=%&:\/~+#-]*[\w@?^=%&\/~+#-])");
                                var match = regex.Match(line);
                                
                                _mergeRequests.Add(match.Value);
                            }
                        }
                        catch { }
                    }

                    if (showUpToDate) AnsiConsole.MarkupLine(UpToDate);
                }
            }

            foreach (var mergeRequest in _mergeRequests)
            {
                Console.WriteLine(mergeRequest);
            }

            return 0;
        }

        private static IEnumerable<NuGetVersion> HighestRevision(IEnumerable<NuGetVersion> versions, NuGetVersion nugetVersion)
        {
            var toReturn = new List<NuGetVersion>();
            var toAdd = versions.Where(v => v.Version.Major == nugetVersion.Major && v.Version.Minor == nugetVersion.Minor && v.Version.Build == nugetVersion.Patch && v.Version.Revision > nugetVersion.Revision).OrderByDescending(v => v).FirstOrDefault();
            if (toAdd != null) toReturn.Add(toAdd);
            return toReturn;
        }

        private static IEnumerable<NuGetVersion> HighestPatch(IEnumerable<NuGetVersion> versions, NuGetVersion nugetVersion)
        {
            var toReturn = new List<NuGetVersion>();
            var toAdd = versions.Where(v => v.Version.Major == nugetVersion.Major && v.Version.Minor == nugetVersion.Minor && v.Version.Build > nugetVersion.Patch).OrderByDescending(v => v).FirstOrDefault();
            if (toAdd != null) toReturn.Add(toAdd);
            return toReturn;
        }

        private static IEnumerable<NuGetVersion> HighestMinor(IEnumerable<NuGetVersion> versions, NuGetVersion nugetVersion)
        {
            var toReturn = new List<NuGetVersion>();
            var toAdd = versions.Where(v => v.Version.Major == nugetVersion.Major && v.Version.Minor > nugetVersion.Minor).OrderByDescending(v => v).FirstOrDefault();
            if (toAdd != null) toReturn.Add(toAdd);
            return toReturn;
        }

        private static IEnumerable<NuGetVersion> HighestMajor(IEnumerable<NuGetVersion> versions, NuGetVersion nugetVersion)
        {
            return versions.Where(v => v.Version.Major > nugetVersion.Major).GroupBy(v => v.Version.Major).Select(g => g.OrderByDescending(v => v).First());
        }

        private class Package
        {
            public string Id { get; set; }
            public string Version { get; set; }
        }

        public class UpdateCommandSettings : CommandSettings
        {
            [Description("Show version information")]
            [CommandOption("-v||--version")]
            public bool Version { get; set; }

            [Description("A root directory to search (default: current directory)")]
            [CommandOption("-d|--directory")]
            public string Directory { get; set; }

            [Description("A NuGet package to update (default: all)")]
            [CommandOption("-p|--package")]
            public string Package { get; set; }

            [Description("Include subdirectories when looking for csproj files (default: true)")]
            [CommandOption("-r|--recursive")]
            [DefaultValue(true)]
            public bool Recursive { get; set; }

            [Description("Include prerelease versions in suggested updates (default: true)")]
            [CommandOption("-i|--includeprerelease")]
            [DefaultValue(true)]
            public bool IncludePrerelease { get; set; }
            
            [Description("Checkout to the source branch (default: empty)")]
            [CommandOption("-s|--sourcebranch")]
            [DefaultValue("")]
            public string SourceBranch { get; set; }
            
            [Description("Checkout to the new branch (default: empty)")]
            [CommandOption("-t|--targetbranch")]
            [DefaultValue("")]
            public string TargetBranch { get; set; }
            
            [Description("Commits updated packages with message (default: empty)")]
            [CommandOption("-c|--commit")]
            [DefaultValue("")]
            public string Commit { get; set; }
            
            [Description("Push target branch (default: false)")]
            [CommandOption("--push")]
            [DefaultValue(false)]
            public bool PushTargetBranch { get; set; }
        }
    }
}
