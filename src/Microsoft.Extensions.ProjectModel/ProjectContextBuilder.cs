﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.ProjectModel.Graph;
using Microsoft.Extensions.ProjectModel.Resolution;
using NuGet.Frameworks;

namespace Microsoft.Extensions.ProjectModel
{
    public class ProjectContextBuilder
    {
        public Project Project { get; set; }

        public LockFile LockFile { get; set; }

        public GlobalSettings GlobalSettings { get; set; }

        public NuGetFramework TargetFramework { get; set; }

        public IEnumerable<string> RuntimeIdentifiers { get; set; } = Enumerable.Empty<string>();

        public string RootDirectory { get; set; }

        public string ProjectDirectory { get; set; }

        public string PackagesDirectory { get; set; }

        public ProjectContext Build()
        {
            ProjectDirectory = Project?.ProjectDirectory ?? ProjectDirectory;

            if (GlobalSettings == null)
            {
                GlobalSettings globalSettings;
                if (GlobalSettings.TryGetGlobalSettings(ProjectDirectory, out globalSettings))
                {
                    GlobalSettings = globalSettings;
                }
            }

            RootDirectory = GlobalSettings?.DirectoryPath ?? RootDirectory;
            PackagesDirectory = PackagesDirectory ?? PackageDependencyProvider.ResolveRepositoryPath(RootDirectory, GlobalSettings);

            LockFileLookup lockFileLookup = null;

            EnsureProjectLoaded();

            var projectLockJsonPath = Path.Combine(ProjectDirectory, LockFile.FileName);

            if (LockFile == null && File.Exists(projectLockJsonPath))
            {
                LockFile = LockFileReader.Read(projectLockJsonPath);
            }

            var validLockFile = true;
            string lockFileValidationMessage = null;

            if (LockFile != null)
            {
                validLockFile = (LockFile.Version == LockFile.CurrentVersion) && LockFile.IsValidForProject(Project, out lockFileValidationMessage);

                lockFileLookup = new LockFileLookup(LockFile);
            }

            var libraries = new Dictionary<string, LibraryDescription>();
            var projectResolver = new ProjectDependencyProvider();

            var mainProject = projectResolver.GetDescription(TargetFramework, Project);

            // Add the main project
            libraries.Add(mainProject.Identity.Name, mainProject);

            LockFileTarget target = null;
            if (lockFileLookup != null)
            {
                target = SelectTarget(LockFile);
                if (target != null)
                {
                    var packageResolver = new PackageDependencyProvider(PackagesDirectory);
                    ScanLibraries(target, lockFileLookup, libraries, packageResolver, projectResolver);
                }
            }

            var frameworkReferenceResolver = new FrameworkReferenceResolver();
            var referenceAssemblyDependencyResolver = new ReferenceAssemblyDependencyResolver(frameworkReferenceResolver);

            // Resolve the dependencies
            ResolveDependencies(libraries, referenceAssemblyDependencyResolver);

            var diagnostics = new List<DiagnosticMessage>();

            // REVIEW: Should this be in NuGet (possibly stored in the lock file?)
            if (LockFile == null)
            {
                diagnostics.Add(new DiagnosticMessage(
                    ErrorCodes.NU1009,
                    $"The expected lock file doesn't exist. Please run \"dnu restore\" to generate a new lock file.",
                    Path.Combine(Project.ProjectDirectory, LockFile.FileName),
                    DiagnosticMessageSeverity.Error));
            }

            if (!validLockFile)
            {
                diagnostics.Add(new DiagnosticMessage(
                    ErrorCodes.NU1006,
                    $"{lockFileValidationMessage}. Please run \"dnu restore\" to generate a new lock file.",
                    Path.Combine(Project.ProjectDirectory, LockFile.FileName),
                    DiagnosticMessageSeverity.Warning));
            }

            // Create a library manager
            var libraryManager = new LibraryManager(libraries.Values.ToList(), diagnostics);

            return new ProjectContext(
                GlobalSettings,
                mainProject,
                TargetFramework,
                target?.RuntimeIdentifier,
                PackagesDirectory,
                libraryManager);
        }

        private void ResolveDependencies(Dictionary<string, LibraryDescription> libraries, ReferenceAssemblyDependencyResolver referenceAssemblyDependencyResolver)
        {
            foreach (var library in libraries.Values.ToList())
            {
                if (Equals(library.Identity.Type, LibraryType.Package) &&
                    !Directory.Exists(library.Path))
                {
                    // If the package path doesn't exist then mark this dependency as unresolved
                    library.Resolved = false;
                }

                library.Framework = library.Framework ?? TargetFramework;
                foreach (var dependency in library.Dependencies)
                {
                    LibraryDescription dep;
                    if (!libraries.TryGetValue(dependency.Name, out dep))
                    {
                        if (Equals(LibraryType.ReferenceAssembly, dependency.Target))
                        {
                            dep = referenceAssemblyDependencyResolver.GetDescription(dependency, TargetFramework) ??
                                  UnresolvedDependencyProvider.GetDescription(dependency, TargetFramework);

                            dep.Framework = TargetFramework;
                            libraries[dependency.Name] = dep;
                        }
                        else
                        {
                            dep = UnresolvedDependencyProvider.GetDescription(dependency, TargetFramework);
                            libraries[dependency.Name] = dep;
                        }
                    }

                    dep.RequestedRanges.Add(dependency);
                    dep.Parents.Add(library);
                }
            }
        }

        private void ScanLibraries(LockFileTarget target, LockFileLookup lockFileLookup, Dictionary<string, LibraryDescription> libraries, PackageDependencyProvider packageResolver, ProjectDependencyProvider projectResolver)
        {
            foreach (var library in target.Libraries)
            {
                if (string.Equals(library.Type, "project"))
                {
                    var projectLibrary = lockFileLookup.GetProject(library.Name);

                    var path = Path.GetFullPath(Path.Combine(ProjectDirectory, projectLibrary.Path));

                    var projectDescription = projectResolver.GetDescription(library.Name, path, library);

                    libraries.Add(projectDescription.Identity.Name, projectDescription);
                }
                else
                {
                    var packageEntry = lockFileLookup.GetPackage(library.Name, library.Version);

                    var packageDescription = packageResolver.GetDescription(packageEntry, library);

                    libraries.Add(packageDescription.Identity.Name, packageDescription);
                }
            }
        }

        private void EnsureProjectLoaded()
        {
            if (Project == null)
            {
                Project project;
                if (ProjectReader.TryGetProject(ProjectDirectory, out project))
                {
                    Project = project;
                }
                else
                {
                    throw new InvalidOperationException($"Unable to resolve project from {ProjectDirectory}");
                }
            }
        }

        private LockFileTarget SelectTarget(LockFile lockFile)
        {
            foreach (var runtimeIdentifier in RuntimeIdentifiers)
            {
                foreach (var scanTarget in lockFile.Targets)
                {
                    if (Equals(scanTarget.TargetFramework, TargetFramework) && string.Equals(scanTarget.RuntimeIdentifier, runtimeIdentifier, StringComparison.Ordinal))
                    {
                        return scanTarget;
                    }
                }
            }

            foreach (var scanTarget in lockFile.Targets)
            {
                if (Equals(scanTarget.TargetFramework, TargetFramework) && string.IsNullOrEmpty(scanTarget.RuntimeIdentifier))
                {
                    return scanTarget;
                }
            }

            return null;
        }
    }
}
