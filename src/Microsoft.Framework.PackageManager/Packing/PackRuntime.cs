// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NuGet;

namespace Microsoft.Framework.PackageManager.Packing
{
    public class PackRuntime
    {
        //private readonly NuGetDependencyResolver _nugetDependencyResolver;
        //private readonly Library _library;
        private readonly FrameworkName _frameworkName;
        private readonly string _kreNupkgPath;

        public PackRuntime(
            PackRoot root,
            FrameworkName frameworkName,
            string kreNupkgPath)
        {
            _frameworkName = frameworkName;
            _kreNupkgPath = kreNupkgPath;
            Name = Path.GetFileName(Path.GetDirectoryName(_kreNupkgPath));
            TargetPath = Path.Combine(root.PackagesPath, Name);
        }

        public string Name { get; private set; }
        public string TargetPath { get; private set; }
        public FrameworkName Framework { get { return _frameworkName; } }

        public void Emit(PackRoot root)
        {
            Console.WriteLine("Packing runtime {0}", Name);

            if (Directory.Exists(TargetPath))
            {
                Console.WriteLine("  {0} already exists.", TargetPath);
                return;
            }

            if (!Directory.Exists(TargetPath))
            {
                Directory.CreateDirectory(TargetPath);
            }

            var targetNupkgPath = Path.Combine(TargetPath, Name + ".nupkg");
            using (var sourceStream = File.OpenRead(_kreNupkgPath))
            {
                using (var archive = new ZipArchive(sourceStream, ZipArchiveMode.Read))
                {
                    root.Operations.ExtractNupkg(archive, TargetPath);
                }
            }
            using (var sourceStream = File.OpenRead(_kreNupkgPath))
            {
                using (var targetStream = new FileStream(targetNupkgPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    sourceStream.CopyTo(targetStream);
                }

                sourceStream.Seek(0, SeekOrigin.Begin);
                var sha512Bytes = SHA512.Create().ComputeHash(sourceStream);
                File.WriteAllText(targetNupkgPath + ".sha512", Convert.ToBase64String(sha512Bytes));
            }
        }
    }
}