// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;

namespace Microsoft.Framework.Runtime.Roslyn
{
    public class RoslynProjectReference : IRoslynMetadataReference, IMetadataProjectReference
    {
        public RoslynProjectReference(CompilationContext compilationContext)
        {
            CompilationContext = compilationContext;
            MetadataReference = compilationContext.Compilation.ToMetadataReference(embedInteropTypes: compilationContext.Project.EmbedInteropTypes);
            Name = compilationContext.Project.Name;
        }

        public CompilationContext CompilationContext { get; private set; }

        public MetadataReference MetadataReference
        {
            get;
            private set;
        }

        public string Name
        {
            get;
            private set;
        }

        public string ProjectPath
        {
            get
            {
                return CompilationContext.Project.ProjectFilePath;
            }
        }

        public IDiagnosticResult GetDiagnostics()
        {
            var diagnostics = CompilationContext.Diagnostics
                .Concat(CompilationContext.Compilation.GetDiagnostics());

            return CreateDiagnosticResult(success: true, diagnostics: diagnostics);
        }

        public IList<ISourceReference> GetSources()
        {
            // REVIEW: Raw sources?
            return CompilationContext.Compilation
                                     .SyntaxTrees
                                     .Select(t => t.FilePath)
                                     .Where(path => !string.IsNullOrEmpty(path))
                                     .Select(path => (ISourceReference)new SourceFileReference(path))
                                     .ToList();
        }

        public Assembly Load(IAssemblyLoaderEngine loaderEngine)
        {
            using (var pdbStream = new MemoryStream())
            using (var assemblyStream = new MemoryStream())
            {
                IList<ResourceDescription> resources = CompilationContext.Resources;

                Trace.TraceInformation("[{0}]: Emitting assembly for {1}", GetType().Name, Name);

                var sw = Stopwatch.StartNew();

                EmitResult emitResult = null;

                //diasmreader not working on CoreSystemServer yet: SET KRE_ROSLYN_EMIT_NOPDB=1
                if ((PlatformHelper.IsMono) || (Environment.GetEnvironmentVariable("KRE_ROSLYN_EMIT_NOPDB") == "1"))
                {
                    // Pdbs aren't supported on mono
                    emitResult = CompilationContext.Compilation.Emit(assemblyStream, manifestResources: resources);
                }
                else
                {
                    emitResult = CompilationContext.Compilation.Emit(assemblyStream, pdbStream: pdbStream, manifestResources: resources);
                }

                sw.Stop();

                Trace.TraceInformation("[{0}]: Emitted {1} in {2}ms", GetType().Name, Name, sw.ElapsedMilliseconds);

                var diagnostics = CompilationContext.Diagnostics.Concat(
                    emitResult.Diagnostics);

                if (!emitResult.Success ||
                    diagnostics.Any(RoslynDiagnosticUtilities.IsError))
                {
                    throw new RoslynCompilationException(diagnostics);
                }

                Assembly assembly = null;

                // Rewind the stream
                assemblyStream.Seek(0, SeekOrigin.Begin);
                pdbStream.Seek(0, SeekOrigin.Begin);

                if (pdbStream.Length == 0)
                {
                    assembly = loaderEngine.LoadStream(assemblyStream, pdbStream: null);
                }
                else
                {
                    assembly = loaderEngine.LoadStream(assemblyStream, pdbStream);
                }

                return assembly;
            }
        }

        public void EmitReferenceAssembly(Stream stream)
        {
            CompilationContext.Compilation.EmitMetadataOnly(stream);
        }

        public IDiagnosticResult EmitAssembly(string outputPath)
        {
            IList<ResourceDescription> resources = CompilationContext.Resources;

            var assemblyPath = Path.Combine(outputPath, Name + ".dll");
            var pdbPath = Path.Combine(outputPath, Name + ".pdb");
            var xmlDocPath = Path.Combine(outputPath, Name + ".xml");

            // REVIEW: Memory bloat?
            using (var xmlDocStream = new MemoryStream())
            using (var pdbStream = new MemoryStream())
            using (var assemblyStream = new MemoryStream())
            {
                Trace.TraceInformation("[{0}]: Emitting assembly for {1}", GetType().Name, Name);

                var sw = Stopwatch.StartNew();

                EmitResult result = null;

                //diasmreader not working on CoreSystemServer yet: SET KRE_ROSLYN_EMIT_NOPDB=1
                if ((PlatformHelper.IsMono) || (Environment.GetEnvironmentVariable("KRE_ROSLYN_EMIT_NOPDB") == "1"))
                {
                    // No pdb support yet
                    result = CompilationContext.Compilation.Emit(assemblyStream, outputName: Path.GetFileName(assemblyPath), pdbFilePath: null, pdbStream: null, xmlDocumentationStream: xmlDocStream, manifestResources: resources);
                }
                else
                {
                    result = CompilationContext.Compilation.Emit(assemblyStream, outputName: Path.GetFileName(assemblyPath), pdbFilePath: pdbPath, pdbStream: pdbStream, xmlDocumentationStream: xmlDocStream, manifestResources: resources);
                }

                sw.Stop();

                Trace.TraceInformation("[{0}]: Emitted {1} in {2}ms", GetType().Name, Name, sw.ElapsedMilliseconds);

                var diagnostics = new List<Diagnostic>(CompilationContext.Diagnostics);
                diagnostics.AddRange(result.Diagnostics);

                if (!result.Success ||
                    diagnostics.Any(RoslynDiagnosticUtilities.IsError))
                {
                    return CreateDiagnosticResult(result.Success, diagnostics);
                }

                // Ensure there's an output directory
                Directory.CreateDirectory(outputPath);

                assemblyStream.Position = 0;
                pdbStream.Position = 0;
                xmlDocStream.Position = 0;

                using (var assemblyFileStream = File.Create(assemblyPath))
                {
                    assemblyStream.CopyTo(assemblyFileStream);
                }

                using (var xmlDocFileStream = File.Create(xmlDocPath))
                {
                    xmlDocStream.CopyTo(xmlDocFileStream);
                }

                if (!PlatformHelper.IsMono)
                {
                    using (var pdbFileStream = File.Create(pdbPath))
                    {
                        pdbStream.CopyTo(pdbFileStream);
                    }
                }

                return CreateDiagnosticResult(result.Success, diagnostics);
            }
        }

        private static DiagnosticResult CreateDiagnosticResult(bool success, IEnumerable<Diagnostic> diagnostics)
        {
            var formatter = new DiagnosticFormatter();

            var errors = diagnostics.Where(RoslynDiagnosticUtilities.IsError)
                                .Select(d => formatter.Format(d)).ToList();

            var warnings = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning)
                                  .Select(d => formatter.Format(d)).ToList();

            return new DiagnosticResult(success, warnings, errors);
        }
    }
}
