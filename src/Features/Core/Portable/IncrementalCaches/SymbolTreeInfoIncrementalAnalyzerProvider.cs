// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.FindSymbols.SymbolTree;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.SolutionCrawler;
using Roslyn.Utilities;
using static Roslyn.Utilities.PortableShim;

namespace Microsoft.CodeAnalysis.IncrementalCaches
{
    [Shared]
    [ExportIncrementalAnalyzerProvider(WorkspaceKind.Host)]
    [ExportWorkspaceServiceFactory(typeof(ISymbolTreeInfoCacheService))]
    internal class SymbolTreeInfoIncrementalAnalyzerProvider : IIncrementalAnalyzerProvider, IWorkspaceServiceFactory
    {
        private class ProjectInfo
        {
            public readonly VersionStamp VersionStamp;
            public readonly SymbolTreeInfo SymbolTreeInfo;

            public ProjectInfo(VersionStamp versionStamp, SymbolTreeInfo info)
            {
                VersionStamp = versionStamp;
                SymbolTreeInfo = info;
            }
        }

        private class MetadataInfo
        {
            public readonly DateTime TimeStamp;
            public readonly SymbolTreeInfo SymbolTreeInfo;

            /// <summary>
            /// Note: the Incremental-Analyzer infrastructure guarantees that it will call all the methods
            /// on <see cref="IncrementalAnalyzer"/> in a serial fashion.  As that is the only type that
            /// reads/writes these <see cref="MetadataInfo"/> objects, we don't need to lock this.
            /// </summary>
            public readonly HashSet<ProjectId> ReferencingProjects;

            public MetadataInfo(DateTime timeStamp, SymbolTreeInfo info, HashSet<ProjectId> referencingProjects)
            {
                TimeStamp = timeStamp;
                SymbolTreeInfo = info;
                ReferencingProjects = referencingProjects;
            }
        }

        // Concurrent dictionaries so they can be read from the SymbolTreeInfoCacheService while 
        // they are being populated/updated by the IncrementalAnalyzer.
        private readonly ConcurrentDictionary<ProjectId, ProjectInfo> _projectToInfo = new ConcurrentDictionary<ProjectId, ProjectInfo>();
        private readonly ConcurrentDictionary<string, MetadataInfo> _metadataPathToInfo = new ConcurrentDictionary<string, MetadataInfo>();

        public IIncrementalAnalyzer CreateIncrementalAnalyzer(Workspace workspace)
        {
            return new IncrementalAnalyzer(_projectToInfo, _metadataPathToInfo);
        }

        public IWorkspaceService CreateService(HostWorkspaceServices workspaceServices)
        {
            return new SymbolTreeInfoCacheService(_projectToInfo, _metadataPathToInfo);
        }

        private static string GetReferenceKey(PortableExecutableReference reference)
        {
            return reference.FilePath ?? reference.Display;
        }

        private static bool TryGetLastWriteTime(string path, out DateTime time)
        {
            var succeeded = false;
            time = IOUtilities.PerformIO(
                () =>
                {
                    var result = File.GetLastWriteTimeUtc(path);
                    succeeded = true;
                    return result;
                },
                default(DateTime));

            return succeeded;
        }

        private class SymbolTreeInfoCacheService : ISymbolTreeInfoCacheService
        {
            private readonly ConcurrentDictionary<ProjectId, ProjectInfo> _projectToInfo;
            private readonly ConcurrentDictionary<string, MetadataInfo> _metadataPathToInfo;

            public SymbolTreeInfoCacheService(
                ConcurrentDictionary<ProjectId, ProjectInfo> projectToInfo,
                ConcurrentDictionary<string, MetadataInfo> metadataPathToInfo)
            {
                _projectToInfo = projectToInfo;
                _metadataPathToInfo = metadataPathToInfo;
            }

            public async Task<SymbolTreeInfo> TryGetSymbolTreeInfoAsync(
                Solution solution,
                IAssemblySymbol assembly,
                PortableExecutableReference reference,
                CancellationToken cancellationToken)
            {
                var key = GetReferenceKey(reference);
                if (key != null)
                {
                    MetadataInfo metadataInfo;
                    if (_metadataPathToInfo.TryGetValue(key, out metadataInfo))
                    {
                        DateTime writeTime;
                        if (TryGetLastWriteTime(key, out writeTime) && writeTime == metadataInfo.TimeStamp)
                        {
                            return metadataInfo.SymbolTreeInfo;
                        }
                    }
                }

                // If we didn't have it in our cache, see if we can load it from disk.
                // Note: pass 'loadOnly' so we only attempt to load from disk, not to actually
                // try to create the metadata.
                var info = await SymbolTreeInfo.TryGetInfoForMetadataAssemblyAsync(
                    solution, assembly, reference, loadOnly: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                return info;
            }

            public async Task<SymbolTreeInfo> TryGetSymbolTreeInfoAsync(
                Project project, CancellationToken cancellationToken)
            {
                ProjectInfo projectInfo;
                if (_projectToInfo.TryGetValue(project.Id, out projectInfo))
                {
                    var version = await project.GetSemanticVersionAsync(cancellationToken).ConfigureAwait(false);
                    if (version == projectInfo.VersionStamp)
                    {
                        return projectInfo.SymbolTreeInfo;
                    }
                }

                return null;
            }
        }

        private class IncrementalAnalyzer : IncrementalAnalyzerBase
        {
            private readonly ConcurrentDictionary<ProjectId, ProjectInfo> _projectToInfo;
            private readonly ConcurrentDictionary<string, MetadataInfo> _metadataPathToInfo;

            private readonly Dictionary<ProjectId, Project> _idTolastSeenProject = new Dictionary<ProjectId, Project>();

            public IncrementalAnalyzer(
                ConcurrentDictionary<ProjectId, ProjectInfo> projectToInfo,
                ConcurrentDictionary<string, MetadataInfo> metadataPathToInfo)
            {
                _projectToInfo = projectToInfo;
                _metadataPathToInfo = metadataPathToInfo;
            }

            public override Task AnalyzeDocumentAsync(Document document, SyntaxNode bodyOpt, CancellationToken cancellationToken)
            {
                if (!document.SupportsSyntaxTree)
                {
                    // Not a language we can produce indices for (i.e. TypeScript).  Bail immediately.
                    return SpecializedTasks.EmptyTask;
                }

                if (bodyOpt != null)
                {
                    // This was a method level edit.  This can't change the symbol tree info
                    // for this project.  Bail immediately.
                    return SpecializedTasks.EmptyTask;
                }

                return UpdateSymbolTreeInfoAsync(document.Project, cancellationToken);
            }
            
            public override Task AnalyzeProjectAsync(Project project, bool semanticsChanged, CancellationToken cancellationToken)
            {
                return UpdateSymbolTreeInfoAsync(project, cancellationToken);
            }

            private async Task UpdateSymbolTreeInfoAsync(Project project, CancellationToken cancellationToken)
            {
                if (!project.SupportsCompilation)
                {
                    return;
                }

                Project lastSeenProject;
                if (_idTolastSeenProject.TryGetValue(project.Id, out lastSeenProject) && lastSeenProject == project)
                {
                    // We already saw this project.  No need to do anything;
                    return;
                }

                // Check the semantic version of this project.  The semantic version will change
                // if any of the source files changed, or if the project version itself changed.
                // (The latter happens when something happens to the project like metadata 
                // changing on disk).
                var version = await project.GetSemanticVersionAsync(cancellationToken).ConfigureAwait(false);

                ProjectInfo projectInfo;
                if (!_projectToInfo.TryGetValue(project.Id, out projectInfo) || projectInfo.VersionStamp != version)
                {
                    await UpdateReferencesAync(project, cancellationToken).ConfigureAwait(false);

                    var info = await SymbolTreeInfo.GetInfoForSourceAssemblyAsync(project, cancellationToken).ConfigureAwait(false);
                    projectInfo = new ProjectInfo(version, info);
                    _projectToInfo.AddOrUpdate(project.Id, projectInfo, (_1, _2) => projectInfo);
                }

                // Mark that we've completed processing this project.
                _idTolastSeenProject[project.Id] = project;
            }

            private async Task UpdateReferencesAync(Project project, CancellationToken cancellationToken)
            {
                foreach (var reference in project.MetadataReferences.OfType<PortableExecutableReference>())
                {
                    await UpdateReferenceAsync(project, reference, cancellationToken).ConfigureAwait(false);
                }
            }

            private async Task UpdateReferenceAsync(
                Project project, PortableExecutableReference reference, CancellationToken cancellationToken)
            {
                var key = GetReferenceKey(reference);
                if (key == null)
                {
                    return;
                }

                DateTime lastWriteTime;
                if (!TryGetLastWriteTime(key, out lastWriteTime))
                {
                    // Couldn't get the write time.  Just ignore this reference.
                    return;
                }

                MetadataInfo metadataInfo;
                if (_metadataPathToInfo.TryGetValue(key, out metadataInfo) && metadataInfo.TimeStamp == lastWriteTime)
                {
                    // We've already computed and cached the info for this reference.
                    return;
                }

                var compilationService = project.LanguageServices.GetService<ICompilationFactoryService>();
                var compilation = compilationService.CreateCompilation("TempAssembly", compilationService.GetDefaultCompilationOptions())
                    .WithReferences(reference);

                var assembly = compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;
                if (assembly == null)
                {
                    return;
                }

                var info = await SymbolTreeInfo.TryGetInfoForMetadataAssemblyAsync(project.Solution, assembly, reference, loadOnly: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                metadataInfo = metadataInfo ?? new MetadataInfo(lastWriteTime, info, new HashSet<ProjectId>());

                // Keep track that this dll is referenced by this project.
                metadataInfo.ReferencingProjects.Add(project.Id);

                _metadataPathToInfo.AddOrUpdate(key, metadataInfo, (_1, _2) => metadataInfo);
            }

            public override void RemoveProject(ProjectId projectId)
            {
                ProjectInfo info;
                _projectToInfo.TryRemove(projectId, out info);
                _idTolastSeenProject.Remove(projectId);

                RemoveMetadataReferences(projectId);
            }

            private void RemoveMetadataReferences(ProjectId projectId)
            {
                foreach (var kvp in _metadataPathToInfo.ToArray())
                {
                    if (kvp.Value.ReferencingProjects.Remove(projectId))
                    {
                        if (kvp.Value.ReferencingProjects.Count == 0)
                        {
                            // This metadata dll isn't referenced by any project.  We can just dump it.
                            MetadataInfo unneeded;
                            _metadataPathToInfo.TryRemove(kvp.Key, out unneeded);
                        }
                    }
                }
            }
        }
    }
}
