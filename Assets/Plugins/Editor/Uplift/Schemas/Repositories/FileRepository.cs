using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Serialization;
using SharpCompress.Archives;
using SharpCompress.Archives.Tar;
using Uplift.Extensions;
using UnityEngine;
using UnityEditor;
using Uplift.Common;

namespace Uplift.Schemas {

    public partial class FileRepository {

        private const string formatPattern = "{0}{1}{2}";

        public override string ToString()
        {
            return "FileRepository " + this.Path;
        }

        public override TemporaryDirectory DownloadPackage(Upset package) {
            TemporaryDirectory td = new TemporaryDirectory();

            string sourcePath = string.Format(formatPattern, Path, System.IO.Path.DirectorySeparatorChar, package.MetaInformation.dirName);

            if (Directory.Exists(sourcePath))
            {

                FileSystemUtil.CopyDirectoryWithMeta(sourcePath, td.Path);
                
            }
            else if (IsUnityPackage(sourcePath))
            {
                using (MemoryStream TarArchiveMS = new MemoryStream())
                {
                    using (FileStream originalFileStream = new FileStream(sourcePath, FileMode.Open))
                    {
                        using (GZipStream decompressionStream =
                            new GZipStream(originalFileStream, CompressionMode.Decompress))
                        {
                            decompressionStream.CopyTo(TarArchiveMS);
                            TarArchiveMS.Position = 0;
                        }
                    }
                    TarArchive reader = TarArchive.Open(TarArchiveMS);

                    string assetPath = null;
                    MemoryStream assetMS = null;
                    MemoryStream metaMS = null;

                    foreach (TarArchiveEntry entry in reader.Entries)
                    {
                        if (entry.IsDirectory) continue;

                        if (entry.Key.EndsWith("asset"))
                        {
                            if (assetMS != null)
                                throw new InvalidOperationException("Unexpected state: assetMS not null");
                            
                            assetMS = new MemoryStream();
                            entry.WriteTo(assetMS);
                            assetMS.Position = 0;
                            continue;
                        }
                        if (entry.Key.EndsWith("metaData"))
                        {
                            throw new NotSupportedException("The package has been packed by a Unity version prior to Unity5, and we do not support this. Contact the package maintainer for updated version.");
                        }
                        if (entry.Key.EndsWith("meta"))
                        {
                            metaMS = new MemoryStream();
                            entry.WriteTo(metaMS);
                            metaMS.Position = 0;
                            continue;
                        }
                        if (entry.Key.EndsWith("pathname"))
                        {
                            MemoryStream MSM = new MemoryStream();
                            entry.WriteTo(MSM);
                            MSM.Position = 0;
                            using (StreamReader SR = new StreamReader(MSM))
                            {
                                assetPath = SR.ReadToEnd().Split('\n')[0];
                            }
                        }
                        if (assetPath != null)
                        {
                            if (assetMS == null)
                            {
                                // these are for directories inside the file
                                Debug.Log("path not null " + assetPath + " but asset not yet read");
                                assetPath = null;
                                continue;
                            }
                            string AssetPath = td.Path + System.IO.Path.DirectorySeparatorChar + assetPath.Replace('/', System.IO.Path.DirectorySeparatorChar);
                            var AssetPathDir = new FileInfo(AssetPath).Directory.FullName;
                            if (!Directory.Exists(AssetPathDir))
                            {
                                Directory.CreateDirectory(AssetPathDir);
                            }
                            using (FileStream FS = new FileStream(AssetPath, FileMode.Create))
                            {
                                assetMS.CopyTo(FS);
                            }
                            assetMS.Dispose();
                            assetMS = null;
                            if (metaMS != null)
                            {
                                string MetaPath = AssetPath + ".meta";
                                using (FileStream FS = new FileStream(MetaPath, FileMode.Create))
                                {
                                    metaMS.CopyTo(FS);
                                }
                                metaMS.Dispose();
                                metaMS = null;
                            }

                            assetPath = null;
                        }
                    }
                }

                string commonRoot = "";
                string[] directories = Directory.GetDirectories(td.Path);
                string inspectedPath = td.Path;

                // Lone directory
                while(directories.Length == 1)
                {
                    string[] entries = Directory.GetFiles(inspectedPath);
                    if (entries.Length > 1 || (entries.Length == 1 && !entries[0].EndsWith(".meta")))
                        break;

                    commonRoot = System.IO.Path.Combine(commonRoot, directories[0]);
                    inspectedPath = System.IO.Path.Combine(td.Path, commonRoot);
                    directories = Directory.GetDirectories(inspectedPath);
                }

                if(commonRoot != "")
                {
                    FileSystemUtil.MoveDirectoryContent(inspectedPath, td.Path);
                    Directory.Delete(System.IO.Path.Combine(td.Path, commonRoot.Replace(td.Path, "").Split(System.IO.Path.DirectorySeparatorChar)[1]), true);
                    if (package.Configuration != null)
                        foreach (InstallSpecPath spec in package.Configuration)
                            spec.Path.Replace(commonRoot, "");
                }
            }
            else
            {
                Debug.LogError(string.Format("Package {0} version {1} has an unexpected format and cannot be downloaded ", package.PackageName, package.PackageVersion));
            }

            return td;
        }

        private static bool IsUnityPackage(string Path)
        {
            return File.Exists(Path) && ".unitypackage".Equals(System.IO.Path.GetExtension(Path), StringComparison.CurrentCultureIgnoreCase);
        }

        public override Upset[] ListPackages() {
            List<Upset> upsetList = new List<Upset>();

            AddExplodedDirectories(upsetList);
            AddUnityPackages(upsetList);

            return upsetList.ToArray();
        }

        private void AddExplodedDirectories(List<Upset> upsetList) {
            string[] directories =  Directory.GetDirectories(Path);
            foreach(string directoryPath in directories)
            {
                string directoryName = directoryPath.Split(System.IO.Path.DirectorySeparatorChar).Last();

                // Don't look at me. System.IO.Path.Combine(string, string, string) doesn't work in Unity :(
                char SC = System.IO.Path.DirectorySeparatorChar;
                string upsetPath = Path + SC + directoryName + SC + UpsetFile;

                if (!File.Exists(upsetPath)) continue;

                XmlSerializer serializer = new XmlSerializer(typeof(Upset));

                using (FileStream file = new FileStream(upsetPath, FileMode.Open)) {
                    Upset upset = serializer.Deserialize(file) as Upset;
                    if(upset.Configuration != null && upset.Configuration.Length != 0)
                    {
                        foreach(InstallSpecPath spec in upset.Configuration)
                        {
                            spec.Path = FileSystemUtil.MakePathOSFriendly(spec.Path);
                        }
                    }
                    upset.MetaInformation.dirName = directoryName;
                    upsetList.Add(upset);
                }
            }
        }

        private void AddUnityPackages(List<Upset> upsetList) {
            string[] files =  Directory.GetFiles(Path, "*.*");

            foreach(string FileName in files)
            {
                if (!IsUnityPackage(FileName)) {
                    continue;
                }
                // assume unityPackage doesn't contain an upset file for now. In the future we can support it
                Upset upset = LoadUpsetOrInfer(FileName);
                if (upset == null) continue;
                upsetList.Add(upset);
            }
        }

        private static Upset LoadUpsetOrInfer(string packagePath)
        {
            string upsetPath = packagePath.Replace(".unitypackage", ".Upset.xml");

            if (File.Exists(upsetPath))
            {
                XmlSerializer serializer = new XmlSerializer(typeof(Upset));

                using (FileStream file = new FileStream(upsetPath, FileMode.Open))
                {
                    Upset upset = serializer.Deserialize(file) as Upset;
                    if (upset.Configuration != null && upset.Configuration.Length != 0)
                    {
                        foreach (InstallSpecPath spec in upset.Configuration)
                        {
                            spec.Path = FileSystemUtil.MakePathOSFriendly(spec.Path);
                        }
                    }
                    upset.MetaInformation.dirName = packagePath.Split(System.IO.Path.DirectorySeparatorChar).Last();

                    return upset;
                }
            }
            else
            {
                return InferUpsetFromUnityPackage(packagePath);
            }
        }

        private static Upset InferUpsetFromUnityPackage(string FileName)
        {
            string ShortFileName = System.IO.Path.GetFileNameWithoutExtension(FileName);
            string[] split = ShortFileName.Split('-');
            if (split.Length != 2)
            {
                Debug.LogWarning("Skipping file " + FileName + " as it doesn't follow the pattern 'PackageName-PackageVersion.unitypackage'");
                return null;
            }
            string PackageName = split[0];
            string PackageVersion = split[1];
            string PackageLicense = "Unknown";
            string MinUnityVersion = "0.0.0";

            Upset upset = new Upset();
            upset.PackageLicense = PackageLicense;
            upset.PackageName = PackageName;
            upset.PackageVersion = PackageVersion;
            upset.UnityVersion = new VersionSpec();
            upset.UnityVersion.ItemElementName = ItemChoiceType.MinVersion;
            upset.UnityVersion.Item = MinUnityVersion;
            upset.MetaInformation.dirName = FileName.Split(System.IO.Path.DirectorySeparatorChar).Last();

            // we need to move things around here
            // upset.InstallSpecifications = new InstallSpec[0];
            return upset;
        }
    }
}
