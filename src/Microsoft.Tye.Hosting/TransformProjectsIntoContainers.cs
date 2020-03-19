﻿using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Tye.Hosting.Model;
using Microsoft.Extensions.Logging;

namespace Microsoft.Tye.Hosting
{
    public class TransformProjectsIntoContainers : IApplicationProcessor
    {
        private readonly ILogger _logger;

        public TransformProjectsIntoContainers(ILogger logger)
        {
            _logger = logger;
        }

        public async Task StartAsync(Model.Application application)
        {
            // This transforms a ProjectRunInfo into

            foreach (var s in application.Services.Values)
            {
                if (s.Description.RunInfo is ProjectRunInfo project)
                {
                    await TransformProjectToContainer(application, s, project);
                }
            }
        }

        private async Task TransformProjectToContainer(Model.Application application, Model.Service service, ProjectRunInfo project)
        {
            var serviceDescription = service.Description;
            var serviceName = serviceDescription.Name;

            var expandedProject = Environment.ExpandEnvironmentVariables(project.Project);
            var fullProjectPath = Path.GetFullPath(Path.Combine(application.ContextDirectory, expandedProject));
            service.Status.ProjectFilePath = fullProjectPath;

            // Sometimes building can fail because of file locking (like files being open in VS)
            _logger.LogInformation("Building project {ProjectFile}", service.Status.ProjectFilePath);

            service.Logs.OnNext($"dotnet build \"{service.Status.ProjectFilePath}\" /nologo");

            var buildResult = await ProcessUtil.RunAsync("dotnet", $"build \"{service.Status.ProjectFilePath}\" /nologo",
                                                        outputDataReceived: data => service.Logs.OnNext(data),
                                                        throwOnError: false);

            if (buildResult.ExitCode != 0)
            {
                _logger.LogInformation("Building {ProjectFile} failed with exit code {ExitCode}: " + buildResult.StandardOutput + buildResult.StandardError, service.Status.ProjectFilePath, buildResult.ExitCode);
                return;
            }

            var targetFramework = GetTargetFramework(service.Status.ProjectFilePath);

            // We transform the project information into the following docker command:
            // docker run -w /app -v {projectDir}:/app -it {image} dotnet /app/bin/Debug/{tfm}/{outputfile}.dll
            var containerImage = DetermineContainerImage(targetFramework);
            var outputFileName = Path.GetFileNameWithoutExtension(service.Status.ProjectFilePath) + ".dll";
            var dockerRunInfo = new DockerRunInfo(containerImage, $"dotnet /app/bin/Debug/{targetFramework}/{outputFileName} {project.Args}")
            {
                WorkingDirectory = "/app"
            };
            dockerRunInfo.VolumeMappings[Path.GetDirectoryName(service.Status.ProjectFilePath)!] = "/app";

            // Change the project into a container info
            serviceDescription.RunInfo = dockerRunInfo;
        }

        private static string DetermineContainerImage(string targetFramework)
        {
            // TODO: Determine the base iamge from the tfm
            return "mcr.microsoft.com/dotnet/core/sdk:3.1-buster";
        }

        private static string GetTargetFramework(string? projectFilePath)
        {
            // TODO: Use msbuild to get the target path
            var debugOutputPath = Path.Combine(Path.GetDirectoryName(projectFilePath)!, "bin", "Debug");

            var tfms = Directory.Exists(debugOutputPath) ? Directory.GetDirectories(debugOutputPath) : Array.Empty<string>();

            return tfms.Select(tfm => new DirectoryInfo(tfm).Name).FirstOrDefault() ?? "netcoreapp3.1";
        }

        public Task StopAsync(Model.Application application)
        {
            return Task.CompletedTask;
        }
    }
}