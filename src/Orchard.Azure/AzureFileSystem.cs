﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.ServiceRuntime;
using Microsoft.WindowsAzure.StorageClient;
using Orchard.FileSystems.Media;

namespace Orchard.Azure {
    public class AzureFileSystem
    {
        private const string FolderEntry = "$$$ORCHARD$$$.$$$";

        public string ContainerName { get; protected set; }

        private readonly CloudStorageAccount _storageAccount;
        private readonly string _root;
        private readonly string _absoluteRoot;
        public CloudBlobClient BlobClient { get; private set; }
        public CloudBlobContainer Container { get; private set; }

        public AzureFileSystem(string containerName, string root, bool isPrivate)
            : this(containerName, root, isPrivate, CloudStorageAccount.Parse(RoleEnvironment.GetConfigurationSettingValue("DataConnectionString"))) {
        }

        public AzureFileSystem(string containerName, string root, bool isPrivate, CloudStorageAccount storageAccount) {
            // Setup the connection to custom storage accountm, e.g. Development Storage
            _storageAccount = storageAccount;
            ContainerName = containerName;
            _root = String.IsNullOrEmpty(root) || root == "/" ? String.Empty : root + "/";
            _absoluteRoot = _storageAccount.BlobEndpoint.AbsoluteUri + containerName + "/" + root + "/";

            using ( new HttpContextWeaver() ) {

                BlobClient = _storageAccount.CreateCloudBlobClient();
                // Get and create the container if it does not exist
                // The container is named with DNS naming restrictions (i.e. all lower case)
                Container = BlobClient.GetContainerReference(ContainerName);

                Container.CreateIfNotExist();

                if (isPrivate) {
                    Container.SetPermissions(new BlobContainerPermissions { PublicAccess = BlobContainerPublicAccessType.Off });
                }
                else {
                    Container.SetPermissions(new BlobContainerPermissions { PublicAccess = BlobContainerPublicAccessType.Container });
                }
            }

        }

        private static void EnsurePathIsRelative(string path) {
            if (path.StartsWith("/") || path.StartsWith("http://"))
                throw new ArgumentException("Path must be relative");
        }

        public IStorageFile GetFile(string path) {
            EnsurePathIsRelative(path);

            using ( new HttpContextWeaver() ) {
                Container.EnsureBlobExists(String.Concat(_root, path));
                return new AzureBlobFileStorage(Container.GetBlockBlobReference(path), _absoluteRoot);
            }
        }

        public bool FileExists(string path) {
            using ( new HttpContextWeaver() ) {
                return Container.BlobExists(String.Concat(_root, path));
            }
        }

        public IEnumerable<IStorageFile> ListFiles(string path) {
            path = path ?? String.Empty;
            
            EnsurePathIsRelative(path);

            string prefix = String.Concat(Container.Name, "/", _root, path);
            
            if ( !prefix.EndsWith("/") )
                prefix += "/";

            using ( new HttpContextWeaver() ) {
                foreach (var blobItem in BlobClient.ListBlobsWithPrefix(prefix).OfType<CloudBlockBlob>()) {
                    // ignore directory entries
                    if(blobItem.Uri.AbsoluteUri.EndsWith(FolderEntry))
                        continue;

                    yield return new AzureBlobFileStorage(blobItem, _absoluteRoot);
                }
            }
        }

        public IEnumerable<IStorageFolder> ListFolders(string path) {
            path = path ?? String.Empty;

            EnsurePathIsRelative(path);
            using ( new HttpContextWeaver() ) {
                if ( !Container.DirectoryExists(String.Concat(_root, path)) ) {
                    try {
                        CreateFolder(String.Concat(_root, path));
                    }
                    catch ( Exception ex ) {
                        throw new ArgumentException(string.Format("The folder could not be created at path: {0}. {1}",
                                                                  path, ex));
                    }
                }

                return Container.GetDirectoryReference(String.Concat(_root, path))
                    .ListBlobs()
                    .OfType<CloudBlobDirectory>()
                    .Select<CloudBlobDirectory, IStorageFolder>(d => new AzureBlobFolderStorage(d, _absoluteRoot))
                    .ToList();
            }
        }

        public void CreateFolder(string path)
        {
            EnsurePathIsRelative(path);
            using (new HttpContextWeaver()) {
                Container.EnsureDirectoryDoesNotExist(String.Concat(_root, path));

                // Creating a virtually hidden file to make the directory an existing concept
                CreateFile(path + "/" + FolderEntry);
            }
        }

        public void DeleteFolder(string path) {
            EnsurePathIsRelative(path);

            using ( new HttpContextWeaver() ) {
                Container.EnsureDirectoryExists(String.Concat(_root, path));
                foreach ( var blob in Container.GetDirectoryReference(String.Concat(_root, path)).ListBlobs() ) {
                    if (blob is CloudBlob)
                        ((CloudBlob) blob).Delete();

                    if (blob is CloudBlobDirectory)
                        DeleteFolder(blob.Uri.ToString().Substring(Container.Uri.ToString().Length + 1 + _root.Length));
                }
            }
        }

        public void RenameFolder(string path, string newPath) {
            EnsurePathIsRelative(path);
            EnsurePathIsRelative(newPath);

            if ( !path.EndsWith("/") )
                path += "/";

            if ( !newPath.EndsWith("/") )
                newPath += "/";
            using ( new HttpContextWeaver() ) {
                foreach (var blob in Container.GetDirectoryReference(_root + path).ListBlobs()) {
                    if (blob is CloudBlob) {
                        string filename = Path.GetFileName(blob.Uri.ToString());
                        string source = String.Concat(path, filename);
                        string destination = String.Concat(newPath, filename);
                        RenameFile(source, destination);
                    }

                    if (blob is CloudBlobDirectory) {
                        string foldername = blob.Uri.Segments.Last();
                        string source = String.Concat(path, foldername);
                        string destination = String.Concat(newPath, foldername);
                        RenameFolder(source, destination);
                    }
                }
            }
        }

        public void DeleteFile(string path) {
            EnsurePathIsRelative(path);
            
            using ( new HttpContextWeaver() ) {
                Container.EnsureBlobExists(path);
                var blob = Container.GetBlockBlobReference(String.Concat(_root, path));
                blob.Delete();
            }
        }

        public void RenameFile(string path, string newPath) {
            EnsurePathIsRelative(path);
            EnsurePathIsRelative(newPath);

            using ( new HttpContextWeaver() ) {
                Container.EnsureBlobExists(String.Concat(_root, path));
                Container.EnsureBlobDoesNotExist(String.Concat(_root, newPath));

                var blob = Container.GetBlockBlobReference(String.Concat(_root, path));
                var newBlob = Container.GetBlockBlobReference(String.Concat(_root, newPath));
                newBlob.CopyFromBlob(blob);
                blob.Delete();
            }
        }

        public IStorageFile CreateFile(string path) {
            EnsurePathIsRelative(path);

            if ( Container.BlobExists(String.Concat(_root, path)) ) {
                throw new ArgumentException("File " + path + " already exists");
            }

            var blob = Container.GetBlockBlobReference(String.Concat(_root, path));
            blob.OpenWrite().Dispose(); // force file creation
            return new AzureBlobFileStorage(blob, _absoluteRoot);
        }

        public string GetPublicUrl(string path) {
            EnsurePathIsRelative(path);
            
            using ( new HttpContextWeaver() ) {
                Container.EnsureBlobExists(String.Concat(_root, path));
                return Container.GetBlockBlobReference(String.Concat(_root, path)).Uri.ToString();
            }
        }

        private class AzureBlobFileStorage : IStorageFile {
            private readonly CloudBlockBlob _blob;
            private readonly string _rootPath;

            public AzureBlobFileStorage(CloudBlockBlob blob, string rootPath) {
                _blob = blob;
                _rootPath = rootPath;
            }

            public string GetPath() {
                return _blob.Uri.ToString().Substring(_rootPath.Length+1);
            }

            public string GetName() {
                return Path.GetFileName(GetPath());
            }

            public long GetSize() {
                return _blob.Properties.Length;
            }

            public DateTime GetLastUpdated() {
                return _blob.Properties.LastModifiedUtc;
            }

            public string GetFileType() {
                return Path.GetExtension(GetPath());
            }

            public Stream OpenRead() {
                return _blob.OpenRead();
            }

            public Stream OpenWrite() {
                return _blob.OpenWrite();
            }

        }

        private class AzureBlobFolderStorage : IStorageFolder {
            private readonly CloudBlobDirectory _blob;
            private readonly string _rootPath;

            public AzureBlobFolderStorage(CloudBlobDirectory blob, string rootPath) {
                _blob = blob;
                _rootPath = rootPath;
            }

            public string GetName() {
                return Path.GetDirectoryName(GetPath() + "/");
            }

            public string GetPath() {
                return _blob.Uri.ToString().Substring(_rootPath.Length + 1).TrimEnd('/');
            }

            public long GetSize() {
                return GetDirectorySize(_blob);
            }

            public DateTime GetLastUpdated() {
                return DateTime.MinValue;
            }

            public IStorageFolder GetParent() {
                if ( _blob.Parent != null ) {
                    return new AzureBlobFolderStorage(_blob.Parent, _rootPath);
                }
                throw new ArgumentException("Directory " + _blob.Uri + " does not have a parent directory");
            }

            private static long GetDirectorySize(CloudBlobDirectory directoryBlob) {
                long size = 0;

                foreach ( var blobItem in directoryBlob.ListBlobs() ) {
                    if ( blobItem is CloudBlob )
                        size += ( (CloudBlob)blobItem ).Properties.Length;

                    if ( blobItem is CloudBlobDirectory )
                        size += GetDirectorySize((CloudBlobDirectory)blobItem);
                }

                return size;
            }
        }

    }
}
