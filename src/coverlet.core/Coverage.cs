using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using Coverlet.Core.Helpers;
using Coverlet.Core.Instrumentation;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Coverlet.Core
{
    public class Coverage
    {
        private readonly string _module;
        private string[] _includeFilters;
        private readonly string[] _includeDirectories;
        private string[] _excludeFilters;
        private readonly string[] _excludedSourceFiles;
        private readonly string[] _excludeAttributes;
        private readonly string[] _sourceLinkFilter;
        private readonly string _mergeWith;
        private readonly bool _useSourceLink;
        private readonly List<InstrumenterResult> _results = new List<InstrumenterResult>();

        private readonly Dictionary<string, MemoryMappedFile> _resultMemoryMaps = new Dictionary<string, MemoryMappedFile>();

        public string Identifier { get; } = Guid.NewGuid().ToString();

        internal IEnumerable<InstrumenterResult> Results => _results;

        public Coverage(string module, string[] includeFilters, string[] includeDirectories, string[] excludeFilters, string[] excludedSourceFiles, string[] excludeAttributes, string mergeWith, bool useSourceLink, string[] sourceLinkFilter)
        {
            _module = module;
            _includeFilters = includeFilters;
            _includeDirectories = includeDirectories ?? Array.Empty<string>();
            _excludeFilters = excludeFilters;
            _excludedSourceFiles = excludedSourceFiles;
            _excludeAttributes = excludeAttributes;
            _mergeWith = mergeWith;
            _useSourceLink = useSourceLink;
            _sourceLinkFilter = sourceLinkFilter;
        }

        public void PrepareModules()
        {
            string[] modules = InstrumentationHelper.GetCoverableModules(_module, _includeDirectories);
            string[] excludes = InstrumentationHelper.GetExcludedFiles(_excludedSourceFiles);
            _excludeFilters = _excludeFilters?.Where(f => InstrumentationHelper.IsValidFilterExpression(f)).ToArray();
            _includeFilters = _includeFilters?.Where(f => InstrumentationHelper.IsValidFilterExpression(f)).ToArray();

            foreach (var module in modules)
            {
                if (InstrumentationHelper.IsModuleExcluded(module, _excludeFilters) ||
                    !InstrumentationHelper.IsModuleIncluded(module, _includeFilters))
                    continue;

                var instrumenter = new Instrumenter(module, Identifier, _excludeFilters, _includeFilters, excludes, _excludeAttributes);
                if (instrumenter.CanInstrument())
                {
                    InstrumentationHelper.BackupOriginalModule(module, Identifier);

                    // Guard code path and restore if instrumentation fails.
                    try
                    {
                        var result = instrumenter.Instrument();
                        _results.Add(result);
                    }
                    catch (Exception)
                    {
                        // TODO: With verbose logging we should note that instrumentation failed.
                        InstrumentationHelper.RestoreOriginalModule(module, Identifier);
                    }
                }
            }

            foreach (var result in _results)
            {
                var size = (result.HitCandidates.Count + ModuleTrackerTemplate.HitsResultHeaderSize) * sizeof(int);

                MemoryMappedFile mmap;

                try
                {
                    // Try using a named memory map not backed by a file (currently only supported on Windows)
                    mmap = MemoryMappedFile.CreateNew(result.HitsResultGuid, size);
                }
                catch (PlatformNotSupportedException)
                {
                    // Fall back on a file-backed memory map
                    mmap = MemoryMappedFile.CreateFromFile(result.HitsFilePath, FileMode.CreateNew, null, size);
                }

                _resultMemoryMaps.Add(result.HitsResultGuid, mmap);
            }
        }

        public CoverageResult GetCoverageResult()
        {
            CalculateCoverage();

            Modules modules = new Modules();
            foreach (var result in _results)
            {
                Documents documents = new Documents();
                foreach (var doc in result.Documents.Values)
                {
                    // Construct Line Results
                    foreach (var line in doc.Lines.Values)
                    {
                        if (documents.TryGetValue(doc.Path, out Classes classes))
                        {
                            if (classes.TryGetValue(line.Class, out Methods methods))
                            {
                                if (methods.TryGetValue(line.Method, out Method method))
                                {
                                    documents[doc.Path][line.Class][line.Method].Lines.Add(line.Number, line.Hits);
                                }
                                else
                                {
                                    documents[doc.Path][line.Class].Add(line.Method, new Method());
                                    documents[doc.Path][line.Class][line.Method].Lines.Add(line.Number, line.Hits);
                                }
                            }
                            else
                            {
                                documents[doc.Path].Add(line.Class, new Methods());
                                documents[doc.Path][line.Class].Add(line.Method, new Method());
                                documents[doc.Path][line.Class][line.Method].Lines.Add(line.Number, line.Hits);
                            }
                        }
                        else
                        {
                            documents.Add(doc.Path, new Classes());
                            documents[doc.Path].Add(line.Class, new Methods());
                            documents[doc.Path][line.Class].Add(line.Method, new Method());
                            documents[doc.Path][line.Class][line.Method].Lines.Add(line.Number, line.Hits);
                        }
                    }

                    // Construct Branch Results
                    foreach (var branch in doc.Branches.Values)
                    {
                        if (documents.TryGetValue(doc.Path, out Classes classes))
                        {
                            if (classes.TryGetValue(branch.Class, out Methods methods))
                            {
                                if (methods.TryGetValue(branch.Method, out Method method))
                                {
                                    method.Branches.Add(new BranchInfo
                                    { Line = branch.Number, Hits = branch.Hits, Offset = branch.Offset, EndOffset = branch.EndOffset, Path = branch.Path, Ordinal = branch.Ordinal }
                                    );
                                }
                                else
                                {
                                    documents[doc.Path][branch.Class].Add(branch.Method, new Method());
                                    documents[doc.Path][branch.Class][branch.Method].Branches.Add(new BranchInfo
                                    { Line = branch.Number, Hits = branch.Hits, Offset = branch.Offset, EndOffset = branch.EndOffset, Path = branch.Path, Ordinal = branch.Ordinal }
                                    );
                                }
                            }
                            else
                            {
                                documents[doc.Path].Add(branch.Class, new Methods());
                                documents[doc.Path][branch.Class].Add(branch.Method, new Method());
                                documents[doc.Path][branch.Class][branch.Method].Branches.Add(new BranchInfo
                                { Line = branch.Number, Hits = branch.Hits, Offset = branch.Offset, EndOffset = branch.EndOffset, Path = branch.Path, Ordinal = branch.Ordinal }
                                );
                            }
                        }
                        else
                        {
                            documents.Add(doc.Path, new Classes());
                            documents[doc.Path].Add(branch.Class, new Methods());
                            documents[doc.Path][branch.Class].Add(branch.Method, new Method());
                            documents[doc.Path][branch.Class][branch.Method].Branches.Add(new BranchInfo
                            { Line = branch.Number, Hits = branch.Hits, Offset = branch.Offset, EndOffset = branch.EndOffset, Path = branch.Path, Ordinal = branch.Ordinal }
                            );
                        }
                    }
                }

                modules.Add(Path.GetFileName(result.ModulePath), documents);
                InstrumentationHelper.RestoreOriginalModule(result.ModulePath, Identifier);
            }

            var coverageResult = new CoverageResult { Identifier = Identifier, Modules = modules, InstrumentedResults = _results };

            if (!string.IsNullOrEmpty(_mergeWith) && !string.IsNullOrWhiteSpace(_mergeWith) && File.Exists(_mergeWith))
            {
                string json = File.ReadAllText(_mergeWith);
                coverageResult.Merge(JsonConvert.DeserializeObject<Modules>(json));
            }

            return coverageResult;
        }

        private void CalculateCoverage()
        {
            foreach (var result in _results)
            {
                List<Document> documents = result.Documents.Values.ToList();
                if (result.SourceLink != null && _useSourceLink && (_sourceLinkFilter == null || _sourceLinkFilter.Contains(result.Module)))
                {
                    var jObject = JObject.Parse(result.SourceLink)["documents"];
                    var sourceLinkDocuments = JsonConvert.DeserializeObject<Dictionary<string, string>>(jObject.ToString());
                    foreach (var document in documents)
                    {
                        document.Path = GetSourceLinkUrl(sourceLinkDocuments, document.Path);
                    }
                }

                // Read hit counts from the memory mapped area, disposing it when done
                using (var mmapFile = _resultMemoryMaps[result.HitsResultGuid])
                {
                    var mmapAccessor = mmapFile.CreateViewAccessor();

                    var unloadStarted = mmapAccessor.ReadInt32(ModuleTrackerTemplate.HitsResultUnloadStarted * sizeof(int));
                    var unloadFinished = mmapAccessor.ReadInt32(ModuleTrackerTemplate.HitsResultUnloadFinished * sizeof(int));

                    if (unloadFinished < unloadStarted)
                    {
                        throw new Exception($"Hit counts only partially reported for {result.Module}");
                    }

                    var documentsList = result.Documents.Values.ToList();

                    for (int i = 0; i < result.HitCandidates.Count; ++i)
                    {
                        var hitLocation = result.HitCandidates[i];
                        var document = documentsList[hitLocation.docIndex];
                        var hits = mmapAccessor.ReadInt32((i + ModuleTrackerTemplate.HitsResultHeaderSize) * sizeof(int));

                        if (hitLocation.isBranch)
                        {
                            var branch = document.Branches[(hitLocation.start, hitLocation.end)];
                            branch.Hits += hits;
                        }
                        else
                        {
                            for (int j = hitLocation.start; j <= hitLocation.end; j++)
                            {
                                var line = document.Lines[j];
                                line.Hits += hits;
                            }
                        }
                    }
                }

                // for MoveNext() compiler autogenerated method we need to patch false positive (IAsyncStateMachine for instance) 
                // we'll remove all MoveNext() not covered branch
                foreach (var document in result.Documents)
                {
                    List<KeyValuePair<(int, int), Branch>> branchesToRemove = new List<KeyValuePair<(int, int), Branch>>();
                    foreach (var branch in document.Value.Branches)
                    {
                        //if one branch is covered we search the other one only if it's not covered
                        if (IsAsyncStateMachineMethod(branch.Value.Method) && branch.Value.Hits > 0)
                        {
                            foreach (var moveNextBranch in document.Value.Branches)
                            {
                                if (moveNextBranch.Value.Method == branch.Value.Method && moveNextBranch.Value != branch.Value && moveNextBranch.Value.Hits == 0)
                                {
                                    branchesToRemove.Add(moveNextBranch);
                                }
                            }
                        }
                    }
                    foreach (var branchToRemove in branchesToRemove)
                    {
                        document.Value.Branches.Remove(branchToRemove.Key);
                    }
                }

                // There's only a hits file on Linux, but if the file doesn't exist this is just a no-op
                InstrumentationHelper.DeleteHitsFile(result.HitsFilePath);
            }
        }

        private bool IsAsyncStateMachineMethod(string method)
        {
            if (!method.EndsWith("::MoveNext()"))
            {
                return false;
            }

            foreach (var instrumentationResult in _results)
            {
                if (instrumentationResult.AsyncMachineStateMethod.Contains(method))
                {
                    return true;
                }
            }
            return false;
        }

        private string GetSourceLinkUrl(Dictionary<string, string> sourceLinkDocuments, string document)
        {
            if (sourceLinkDocuments.TryGetValue(document, out string url))
            {
                return url;
            }

            var keyWithBestMatch = string.Empty;
            var relativePathOfBestMatch = string.Empty;

            foreach (var sourceLinkDocument in sourceLinkDocuments)
            {
                string key = sourceLinkDocument.Key;
                if (Path.GetFileName(key) != "*") continue;

                string relativePath = Path.GetRelativePath(Path.GetDirectoryName(key), Path.GetDirectoryName(document));

                if (relativePath.Contains("..")) continue;

                if (relativePathOfBestMatch.Length == 0)
                {
                    keyWithBestMatch = sourceLinkDocument.Key;
                    relativePathOfBestMatch = relativePath;
                }

                if (relativePath.Length < relativePathOfBestMatch.Length)
                {
                    keyWithBestMatch = sourceLinkDocument.Key;
                    relativePathOfBestMatch = relativePath;
                }
            }

            relativePathOfBestMatch = relativePathOfBestMatch == "." ? string.Empty : relativePathOfBestMatch;

            string replacement = Path.Combine(relativePathOfBestMatch, Path.GetFileName(document));
            replacement = replacement.Replace('\\', '/');

            url = sourceLinkDocuments[keyWithBestMatch];
            return url.Replace("*", replacement);
        }
    }
}
