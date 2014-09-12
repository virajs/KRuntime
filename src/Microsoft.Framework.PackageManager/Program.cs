// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using Microsoft.Framework.PackageManager.Packing;
using Microsoft.Framework.Runtime;
using Microsoft.Framework.Runtime.Common.CommandLine;

namespace Microsoft.Framework.PackageManager
{
    public class Program : IReport
    {
        private readonly IServiceProvider _hostServices;
        private readonly IApplicationEnvironment _environment;

        public Program(IServiceProvider hostServices, IApplicationEnvironment environment)
        {
            _hostServices = hostServices;
            _environment = environment;

#if NET45
            Thread.GetDomain().SetData(".appDomain", this);
            ServicePointManager.DefaultConnectionLimit = 1024;
#endif
        }

        public int Main(string[] args)
        {
            AnsiConsole.OriginalForegroundColor = Console.ForegroundColor;

            var app = new CommandLineApplication();
            app.Name = "kpm";

            var optionVerbose = app.Option("-v|--verbose", "Show verbose output", CommandOptionType.NoValue);
            app.HelpOption("-?|-h|--help");
            app.VersionOption("--version", GetVersion());

            // Show help information if no subcommand was specified
            app.OnExecute(() =>
            {
                app.ShowHelp();
                return 0;
            });

            app.Command("restore", c =>
            {
                c.Description = "Restore packages";

                var argRoot = c.Argument("[root]", "Root of all projects to restore. It can be a directory, a project.json, or a global.json.");
                var optSource = c.Option("-s|--source <FEED>", "A list of packages sources to use for this command",
                    CommandOptionType.MultipleValue);
                var optFallbackSource = c.Option("-f|--fallbacksource <FEED>",
                    "A list of packages sources to use as a fallback", CommandOptionType.MultipleValue);
                var optProxy = c.Option("-p|--proxy <ADDRESS>", "The HTTP proxy to use when retrieving packages",
                    CommandOptionType.SingleValue);
                var optNoCache = c.Option("--no-cache", "Do not use local cache", CommandOptionType.NoValue);
                var optPackageFolder = c.Option("--packages", "Path to restore packages", CommandOptionType.SingleValue);
                var optQuiet = c.Option("--quiet", "Do not show output such as HTTP request/cache information",
                    CommandOptionType.NoValue);
                c.HelpOption("-?|-h|--help");

                c.OnExecute(async () =>
                {
                    var command = new RestoreCommand(_environment);
                    command.Reports = CreateReports(optionVerbose.HasValue(), optQuiet.HasValue());

                    command.RestoreDirectory = argRoot.Value;
                    command.Sources = optSource.Values;
                    command.FallbackSources = optFallbackSource.Values;
                    command.NoCache = optNoCache.HasValue();
                    command.PackageFolder = optPackageFolder.Value();

                    if (optProxy.HasValue())
                    {
                        Environment.SetEnvironmentVariable("http_proxy", optProxy.Value());
                    }

                    var success = await command.ExecuteCommand();

                    return success ? 0 : 1;
                });
            });

            app.Command("pack", c =>
            {
                c.Description = "Bundle application for deployment";

                var argProject = c.Argument("[project]", "Path to project, default is current directory");
                var optionOut = c.Option("-o|--out <PATH>", "Where does it go", CommandOptionType.SingleValue);
                var optionConfiguration = c.Option("--configuration <CONFIGURATION>", "The configuration to use for deployment", CommandOptionType.SingleValue);
                var optionOverwrite = c.Option("--overwrite", "Remove existing files in target folders",
                    CommandOptionType.NoValue);
                var optionNoSource = c.Option("--no-source", "Don't include sources of project dependencies",
                    CommandOptionType.NoValue);
                var optionRuntime = c.Option("--runtime <KRE>", "Names or paths to KRE files to include",
                    CommandOptionType.MultipleValue);
                var optionAppFolder = c.Option("--appfolder <NAME>",
                    "Determine the name of the application primary folder", CommandOptionType.SingleValue);
                var optionNative = c.Option("--native", "Build and include native images. User must provide targeted CoreCLR runtime versions along with this option.",
                    CommandOptionType.NoValue);
                c.HelpOption("-?|-h|--help");

                c.OnExecute(() =>
                {
                    Console.WriteLine("verbose:{0} out:{1} project:{2}",
                        optionVerbose.HasValue(),
                        optionOut.Value(),
                        argProject.Value);

                    var options = new PackOptions
                    {
                        OutputDir = optionOut.Value(),
                        ProjectDir = argProject.Value ?? System.IO.Directory.GetCurrentDirectory(),
                        AppFolder = optionAppFolder.Value(),
                        Configuration = optionConfiguration.Value() ?? "Debug",
                        RuntimeTargetFramework = _environment.RuntimeFramework,
                        Overwrite = optionOverwrite.HasValue(),
                        NoSource = optionNoSource.HasValue(),
                        Runtimes = optionRuntime.HasValue() ?
                            string.Join(";", optionRuntime.Values).
                                Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries) :
                            new string[0],
                        Native = optionNative.HasValue()
                    };

                    var manager = new PackManager(_hostServices, options);
                    if (!manager.Package())
                    {
                        return -1;
                    }

                    return 0;
                });
            });

            app.Command("build", c =>
            {
                c.Description = "Build NuGet packages for the project in given directory";

                var optionFramework = c.Option("--framework <TARGET_FRAMEWORK>", "A list of target frameworks to build.", CommandOptionType.MultipleValue);
                var optionConfiguration = c.Option("--configuration <CONFIGURATION>", "A list of configurations to build.", CommandOptionType.MultipleValue);
                var optionOut = c.Option("--out <OUTPUT_DIR>", "Output directory", CommandOptionType.SingleValue);
                var optionDependencies = c.Option("--dependencies", "Copy dependencies", CommandOptionType.NoValue);
                var argProjectDir = c.Argument("[project]", "Project to build, default is current directory");
                c.HelpOption("-?|-h|--help");

                c.OnExecute(() =>
                {
                    var buildOptions = new BuildOptions();
                    buildOptions.RuntimeTargetFramework = _environment.RuntimeFramework;
                    buildOptions.OutputDir = optionOut.Value();
                    buildOptions.ProjectDir = argProjectDir.Value ?? Directory.GetCurrentDirectory();
                    buildOptions.Configurations = optionConfiguration.Values;
                    buildOptions.TargetFrameworks = optionFramework.Values;

                    var projectManager = new BuildManager(_hostServices, buildOptions);

                    if (!projectManager.Build())
                    {
                        return -1;
                    }

                    return 0;
                });
            });

            app.Command("add", c =>
            {
                c.Description = "Add a dependency into dependencies section of project.json";

                var argName = c.Argument("[name]", "Name of the dependency to add");
                var argVersion = c.Argument("[version]", "Version of the dependency to add");
                var argProject = c.Argument("[project]", "Path to project, default is current directory");
                c.HelpOption("-?|-h|--help");

                c.OnExecute(() =>
                {
                    var command = new AddCommand();
                    command.Report = this;
                    command.Name = argName.Value;
                    command.Version = argVersion.Value;
                    command.ProjectDir = argProject.Value;

                    var success = command.ExecuteCommand();

                    return success ? 0 : 1;
                });
            });

            app.Command("install", c =>
            {
                c.Description = "Install the given dependency";

                var argName = c.Argument("[name]", "Name of the dependency to add");
                var argVersion = c.Argument("[version]", "Version of the dependency to add, default is the latest version.");
                var argProject = c.Argument("[project]", "Path to project, default is current directory");
                var optSource = c.Option("-s|--source <FEED>", "A list of packages sources to use for this command",
                    CommandOptionType.MultipleValue);
                var optFallbackSource = c.Option("-f|--fallbacksource <FEED>",
                    "A list of packages sources to use as a fallback", CommandOptionType.MultipleValue);
                var optProxy = c.Option("-p|--proxy <ADDRESS>", "The HTTP proxy to use when retrieving packages",
                    CommandOptionType.SingleValue);
                var optNoCache = c.Option("--no-cache", "Do not use local cache", CommandOptionType.NoValue);
                var optPackageFolder = c.Option("--packages", "Path to restore packages", CommandOptionType.SingleValue);
                var optQuiet = c.Option("--quiet", "Do not show output such as HTTP request/cache information",
                    CommandOptionType.NoValue);
                c.HelpOption("-?|-h|--help");

                c.OnExecute(async () =>
                {
                    var reports = CreateReports(optionVerbose.HasValue(), optQuiet.HasValue());

                    var addCmd = new AddCommand();
                    addCmd.Report = this;
                    addCmd.Name = argName.Value;
                    addCmd.Version = argVersion.Value;
                    addCmd.ProjectDir = argProject.Value;

                    var restoreCmd = new RestoreCommand(_environment);
                    restoreCmd.Reports = reports;

                    restoreCmd.RestoreDirectory = argProject.Value;
                    restoreCmd.Sources = optSource.Values;
                    restoreCmd.FallbackSources = optFallbackSource.Values;
                    restoreCmd.NoCache = optNoCache.HasValue();
                    restoreCmd.PackageFolder = optPackageFolder.Value();

                    if (optProxy.HasValue())
                    {
                        Environment.SetEnvironmentVariable("http_proxy", optProxy.Value());
                    }

                    var installCmd = new InstallCommand(addCmd, restoreCmd);
                    installCmd.Reports = reports;

                    var success = await installCmd.ExecuteCommand();

                    return success ? 0 : 1;
                });
            });

            return app.Execute(args);
        }

        private Reports CreateReports(bool verbose, bool quiet)
        {
            var reports = new Reports()
            {
                Information = this,
                Verbose = verbose ? (this as IReport) : new NullReport()
            };

            // If "--verbose" and "--quiet" are specified together, "--verbose" wins
            reports.Quiet = quiet ? reports.Verbose : this;

            return reports;
        }

        object _lock = new object();

        public void WriteLine(string message)
        {
            lock (_lock)
            {
                AnsiConsole.WriteLine(message);
            }
        }

        private static string GetVersion()
        {
            var assembly = typeof(Program).GetTypeInfo().Assembly;
            var assemblyInformationalVersionAttribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            return assemblyInformationalVersionAttribute.InformationalVersion;
        }
    }
}
