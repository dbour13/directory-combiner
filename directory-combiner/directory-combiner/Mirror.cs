#undef TRACE

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using directory_combiner;
using DokanNet;
using DokanNet.Logging;
using static DokanNet.FormatProviders;
using FileAccess = DokanNet.FileAccess;

namespace DokanNetMirror
{
    internal class Mirror : IDokanOperations
    {
        private readonly FolderMap[] folderMaps;

        private const FileAccess DataAccess = FileAccess.ReadData | FileAccess.WriteData | FileAccess.AppendData |
                                              FileAccess.Execute |
                                              FileAccess.GenericExecute | FileAccess.GenericWrite |
                                              FileAccess.GenericRead;

        private const FileAccess DataWriteAccess = FileAccess.WriteData | FileAccess.AppendData |
                                                   FileAccess.Delete |
                                                   FileAccess.GenericWrite;

        private readonly ILogger _logger;

        public Mirror(ILogger logger, IEnumerable<FolderMap> folderMaps)
        {
            foreach (var folderMap in folderMaps) 
            {
                if (!Directory.Exists(folderMap.MapFrom))
                {
                    throw new ArgumentException(nameof(Mirror.folderMaps), $"Folder \"{folderMap.MapFrom}\" does not exist");
                }
            }



            _logger = logger;
            this.folderMaps = folderMaps.ToArray();
        }

        /// <summary>
        /// Returns true if path2 is in path1
        /// </summary>
        /// <param name="path1"></param>
        /// <param name="path2"></param>
        /// <returns></returns>
        protected bool IsInPath(string path1, string path2)
        {
            string[] pathParts1 = path1.Split("\\", StringSplitOptions.RemoveEmptyEntries);
            string[] pathParts2 = path2.Split("\\", StringSplitOptions.RemoveEmptyEntries);

            if (pathParts2.Length > pathParts1.Length)
            {
                return false;
            }

            for (int i = 0; i < pathParts2.Length;i++)
            {
                if (!pathParts2[i].Equals(pathParts1[i]))
                {
                    return false;
                }
            }

            return true;
        }

        protected string[] GetDirectoriesInVirtualPath(string virtualPath)
        {
            return folderMaps.Where(fm => IsInPath(fm.MapTo, virtualPath))
                             .Select(fm => fm.MapTo.Substring(virtualPath.Length).Split("\\")[0])
                             .Distinct()
                             .ToArray();
        }

        /// <summary>
        /// Will return a single path for a file, but may return multiple paths for a directory
        /// depending on folderMaps.
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        protected string[] GetPath(string fileName, bool isDirectory)
        {
            // If it's a file
            if (!isDirectory)
            {
                // Only care about that ones that are mapped to MapTo
                foreach (var folderMap in folderMaps.Where(fm => IsInPath(fileName, fm.MapTo)))
                {
                    // Get rid of the MapTo portion from fileName and add it to MapFrom to find
                    // the path on the physical disc it is located
                    string path = folderMap.MapFrom.TrimEnd('\\') + fileName.Substring(folderMap.MapTo.Length);

                    if (File.Exists(path))
                    {
                        return new string[] { path };
                    }
                }
            }
            // If it's a folder
            else
            {
                // Return all folders that are mapped to the folder location at 'fileName'
                return folderMaps.Where(fm => IsInPath(fileName, fm.MapTo))
                                    .Select(fm => fm.MapFrom.TrimEnd('\\') + fileName.Substring(fm.MapTo.Length))
                                    .Where(d => Directory.Exists(d)).ToArray();
            }

            // Otherwise if it doesn't exist yet just use the first folderMap
            return folderMaps.Where(fm => IsInPath(fileName, fm.MapTo))
                             .Select(fm => fm.MapFrom.TrimEnd('\\') + fileName.Substring(fm.MapTo.Length))
                             .Take(1)
                             .ToArray();
        }

        protected NtStatus Trace(string method, string fileName, IDokanFileInfo info, NtStatus result,
            params object[] parameters)
        {
#if TRACE
            var extraParameters = parameters != null && parameters.Length > 0
                ? ", " + string.Join(", ", parameters.Select(x => string.Format(DefaultFormatProvider, "{0}", x)))
                : string.Empty;

            _logger.Debug(DokanFormat($"{method}('{fileName}', {info}{extraParameters}) -> {result}"));
#endif

            return result;
        }

        private NtStatus Trace(string method, string fileName, IDokanFileInfo info,
            FileAccess access, FileShare share, FileMode mode, FileOptions options, FileAttributes attributes,
            NtStatus result)
        {
#if TRACE
            _logger.Debug(
                DokanFormat(
                    $"{method}('{fileName}', {info}, [{access}], [{share}], [{mode}], [{options}], [{attributes}]) -> {result}"));
#endif

            return result;
        }

        protected static Int32 GetNumOfBytesToCopy(Int32 bufferLength, long offset, IDokanFileInfo info, FileStream stream)
        {
            if (info.PagingIo)
            {
                var longDistanceToEnd = stream.Length - offset;
                var isDistanceToEndMoreThanInt = longDistanceToEnd > Int32.MaxValue;
                if (isDistanceToEndMoreThanInt) return bufferLength;
                var distanceToEnd = (Int32)longDistanceToEnd;
                if (distanceToEnd < bufferLength) return distanceToEnd;
                return bufferLength;
            }
            return bufferLength;
        }

        #region Implementation of IDokanOperations

        public void CloseFile(string fileName, IDokanFileInfo info)
        {
#if TRACE
            if (info.Context != null)
                Console.WriteLine(DokanFormat($"{nameof(CloseFile)}('{fileName}', {info} - entering"));
#endif

            (info.Context as FileStream)?.Dispose();
            info.Context = null;
            Trace(nameof(CloseFile), fileName, info, DokanResult.Success);
            // could recreate cleanup code here but this is not called sometimes
        }

        public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
        {
            if (info.Context == null) // memory mapped read
            {
                using (var stream = new FileStream(GetPath(fileName, info.IsDirectory)[0], FileMode.Open, System.IO.FileAccess.Read))
                {
                    stream.Position = offset;
                    bytesRead = stream.Read(buffer, 0, buffer.Length);
                }
            }
            else // normal read
            {
                var stream = info.Context as FileStream;
                lock (stream) //Protect from overlapped read
                {
                    stream.Position = offset;
                    bytesRead = stream.Read(buffer, 0, buffer.Length);
                }
            }
            return Trace(nameof(ReadFile), fileName, info, DokanResult.Success, "out " + bytesRead.ToString(),
                offset.ToString(CultureInfo.InvariantCulture));
        }


        public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
        {
            // may be called with info.Context == null, but usually it isn't
            var filePaths = GetPath(fileName, info.IsDirectory);

            if (filePaths.Any())
            {
                FileSystemInfo finfo = new FileInfo(filePaths[0]);
                if (!finfo.Exists)
                    finfo = new DirectoryInfo(filePaths[0]);

                fileInfo = new FileInformation
                {
                    FileName = fileName,
                    Attributes = finfo.Attributes,
                    CreationTime = finfo.CreationTime,
                    LastAccessTime = finfo.LastAccessTime,
                    LastWriteTime = finfo.LastWriteTime,
                    Length = (finfo as FileInfo)?.Length ?? 0,
                };
            }
            else
            {
                // It's a virtual directory, not from the physical drives
                fileInfo = new FileInformation
                {
                    FileName = fileName,
                    Attributes = FileAttributes.ReadOnly | FileAttributes.Directory,
                    CreationTime = DateTime.Now,
                    LastAccessTime = DateTime.Now,
                    LastWriteTime = DateTime.Now,
                    Length = 0,
                };
            }

            return Trace(nameof(GetFileInformation), fileName, info, DokanResult.Success);
        }

        public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
        {
            // This function is not called because FindFilesWithPattern is implemented
            files = FindFilesHelper(fileName, "*", info.IsDirectory);

            return Trace(nameof(FindFiles), fileName, info, DokanResult.Success);
        }


        public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes, out long totalNumberOfFreeBytes, IDokanFileInfo info)
        {
            var dinfo = DriveInfo.GetDrives().Where(di => 
              folderMaps.Select(fm => Path.GetPathRoot(fm.MapFrom)).Distinct().Any(d => 
                string.Equals(di.RootDirectory.Name, d, StringComparison.OrdinalIgnoreCase)));

            freeBytesAvailable = dinfo.Sum(di => di.TotalFreeSpace);
            totalNumberOfBytes = dinfo.Sum(di => di.TotalSize);
            totalNumberOfFreeBytes = dinfo.Sum(di => di.AvailableFreeSpace);
            return Trace(nameof(GetDiskFreeSpace), null, info, DokanResult.Success, "out " + freeBytesAvailable.ToString(),
                "out " + totalNumberOfBytes.ToString(), "out " + totalNumberOfFreeBytes.ToString());
        }

        public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
            out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
        {
            volumeLabel = "Virtual Drive";
            fileSystemName = "NTFS";
            maximumComponentLength = 256;

            features = FileSystemFeatures.CasePreservedNames | FileSystemFeatures.CaseSensitiveSearch |
                       FileSystemFeatures.PersistentAcls | FileSystemFeatures.SupportsRemoteStorage |
                       FileSystemFeatures.UnicodeOnDisk;

            return Trace(nameof(GetVolumeInformation), null, info, DokanResult.Success, "out " + volumeLabel,
                "out " + features.ToString(), "out " + fileSystemName);
        }

        public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity security, AccessControlSections sections,
            IDokanFileInfo info)
        {
            try
            {
#if NET5_0_OR_GREATER
                security = info.IsDirectory
                    ? (FileSystemSecurity)new DirectoryInfo(GetPath(fileName, info.IsDirectory)[0]).GetAccessControl()
                    : new FileInfo(GetPath(fileName, info.IsDirectory)[0]).GetAccessControl();
#else
                security = info.IsDirectory
                    ? (FileSystemSecurity)Directory.GetAccessControl(GetPath(fileName))
                    : File.GetAccessControl(GetPath(fileName));
#endif
                return Trace(nameof(GetFileSecurity), fileName, info, DokanResult.Success, sections.ToString());
            }
            catch (UnauthorizedAccessException)
            {
                security = null;
                return Trace(nameof(GetFileSecurity), fileName, info, DokanResult.AccessDenied, sections.ToString());
            }
        }

        public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
        {
            return Trace(nameof(Mounted), null, info, DokanResult.Success);
        }


        public NtStatus Unmounted(IDokanFileInfo info)
        {
            return Trace(nameof(Unmounted), null, info, DokanResult.Success);
        }

        public NtStatus FindStreams(string fileName, IntPtr enumContext, out string streamName, out long streamSize,
            IDokanFileInfo info)
        {
            streamName = string.Empty;
            streamSize = 0;
            return Trace(nameof(FindStreams), fileName, info, DokanResult.NotImplemented, enumContext.ToString(),
                "out " + streamName, "out " + streamSize.ToString());
        }

        public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
        {
            streams = new FileInformation[0];
            return Trace(nameof(FindStreams), fileName, info, DokanResult.NotImplemented);
        }

        public IList<FileInformation> FindFilesHelper(string fileName, string searchPattern, bool isDirectory)
        {
            // Get files from all relevant mapped directories
            string[] paths = GetPath(fileName, isDirectory);
            IList<FileInformation> files = null;

            if (paths.Any())
            {
                files = paths.Select(d => new DirectoryInfo(d))
                             .Select(di =>
                             di.EnumerateFileSystemInfos()
                             .Where(finfo => DokanHelper.DokanIsNameInExpression(searchPattern, finfo.Name, true)))
                             .SelectMany(di => di)
                             .Select(finfo => new
                             {
                                 Attributes = finfo.Attributes,
                                 CreationTime = finfo.CreationTime,
                                 LastAccessTime = finfo.LastAccessTime,
                                 LastWriteTime = finfo.LastWriteTime,
                                 Length = (finfo as FileInfo)?.Length ?? 0,
                                 Name = finfo.Name
                             })
                             .Distinct()
                             .Select(finfo => new FileInformation
                             {
                                 Attributes = finfo.Attributes,
                                 CreationTime = finfo.CreationTime,
                                 LastAccessTime = finfo.LastAccessTime,
                                 LastWriteTime = finfo.LastWriteTime,
                                 Length = finfo.Length,
                                 FileName = finfo.Name
                             }).ToArray();
            }
            else
            {
                // It's a virtual path, not from any physical drive
                files = GetDirectoriesInVirtualPath(fileName).Select(fileName => new FileInformation
                {
                    Attributes = FileAttributes.ReadOnly | FileAttributes.Directory,
                    CreationTime = DateTime.Now,
                    LastAccessTime = DateTime.Now,
                    LastWriteTime = DateTime.Now,
                    Length = 0,
                    FileName = fileName
                }).ToArray();
            }

            return files;
        }

        public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files,
            IDokanFileInfo info)
        {
            files = FindFilesHelper(fileName, searchPattern, info.IsDirectory);

            return Trace(nameof(FindFilesWithPattern), fileName, info, DokanResult.Success);
        }






        public NtStatus CreateFile(string fileName, FileAccess access, FileShare share, FileMode mode,
            FileOptions options, FileAttributes attributes, IDokanFileInfo info)
        {
            var result = DokanResult.Success;
            var filePath = GetPath(fileName, info.IsDirectory);

            if (info.IsDirectory)
            {
                try
                {
                    switch (mode)
                    {
                        case FileMode.Open:
                            if (!filePath.Any())
                            {
                                if (!GetDirectoriesInVirtualPath(fileName).Any())
                                {
                                    // It may be that nothing is mapped to this directory.  Will 
                                    return Trace(nameof(CreateFile), fileName, info, access, share, mode, options,
                                        attributes, DokanResult.PathNotFound);
                                }
                            }
                            else if (!Directory.Exists(filePath[0]))
                            {
                                try
                                {
                                    if (!File.GetAttributes(filePath[0]).HasFlag(FileAttributes.Directory))
                                        return Trace(nameof(CreateFile), fileName, info, access, share, mode, options,
                                            attributes, DokanResult.NotADirectory);
                                }
                                catch (Exception)
                                {
                                    return Trace(nameof(CreateFile), fileName, info, access, share, mode, options,
                                        attributes, DokanResult.FileNotFound);
                                }
                                return Trace(nameof(CreateFile), fileName, info, access, share, mode, options,
                                    attributes, DokanResult.PathNotFound);
                            }
                            else
                            {
                                new DirectoryInfo(filePath[0]).EnumerateFileSystemInfos().Any();
                            }

                            // you can't list the directory
                            break;

                        case FileMode.CreateNew:
                            if (Directory.Exists(filePath[0]))
                                return Trace(nameof(CreateFile), fileName, info, access, share, mode, options,
                                    attributes, DokanResult.FileExists);

                            try
                            {
                                File.GetAttributes(filePath[0]).HasFlag(FileAttributes.Directory);
                                return Trace(nameof(CreateFile), fileName, info, access, share, mode, options,
                                    attributes, DokanResult.AlreadyExists);
                            }
                            catch (IOException)
                            {
                            }

                            Directory.CreateDirectory(GetPath(fileName, info.IsDirectory)[0]);
                            break;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                        DokanResult.AccessDenied);
                }
            }
            else
            {
                var pathExists = true;
                var pathIsDirectory = false;

                var readWriteAttributes = (access & DataAccess) == 0;
                var readAccess = (access & DataWriteAccess) == 0;

                try
                {
                    pathExists = (filePath.Any() && (Directory.Exists(filePath[0]) || File.Exists(filePath[0])));
                    pathIsDirectory = pathExists ? File.GetAttributes(filePath[0]).HasFlag(FileAttributes.Directory) : false;
                }
                catch (IOException)
                {
                }

                switch (mode)
                {
                    case FileMode.Open:

                        if (pathExists)
                        {
                            // check if driver only wants to read attributes, security info, or open directory
                            if (readWriteAttributes || pathIsDirectory)
                            {
                                if (pathIsDirectory && (access & FileAccess.Delete) == FileAccess.Delete
                                    && (access & FileAccess.Synchronize) != FileAccess.Synchronize)
                                    //It is a DeleteFile request on a directory
                                    return Trace(nameof(CreateFile), fileName, info, access, share, mode, options,
                                        attributes, DokanResult.AccessDenied);

                                info.IsDirectory = pathIsDirectory;
                                info.Context = new object();
                                // must set it to something if you return DokanError.Success

                                return Trace(nameof(CreateFile), fileName, info, access, share, mode, options,
                                    attributes, DokanResult.Success);
                            }
                        }
                        else
                        {
                            return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                                DokanResult.FileNotFound);
                        }
                        break;

                    case FileMode.CreateNew:
                        if (pathExists)
                            return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                                DokanResult.FileExists);
                        break;

                    case FileMode.Truncate:
                        if (!pathExists)
                            return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                                DokanResult.FileNotFound);
                        break;
                }

                try
                {
                    info.Context = new FileStream(filePath[0], mode,
                        readAccess ? System.IO.FileAccess.Read : System.IO.FileAccess.ReadWrite, share, 4096, options);

                    if (pathExists && (mode == FileMode.OpenOrCreate
                                       || mode == FileMode.Create))
                        result = DokanResult.AlreadyExists;

                    bool fileCreated = mode == FileMode.CreateNew || mode == FileMode.Create || (!pathExists && mode == FileMode.OpenOrCreate);
                    if (fileCreated)
                    {
                        FileAttributes new_attributes = attributes;
                        new_attributes |= FileAttributes.Archive; // Files are always created as Archive
                        // FILE_ATTRIBUTE_NORMAL is override if any other attribute is set.
                        new_attributes &= ~FileAttributes.Normal;
                        File.SetAttributes(filePath[0], new_attributes);
                    }
                }
                catch (UnauthorizedAccessException) // don't have access rights
                {
                    if (info.Context is FileStream fileStream)
                    {
                        // returning AccessDenied cleanup and close won't be called,
                        // so we have to take care of the stream now
                        fileStream.Dispose();
                        info.Context = null;
                    }
                    return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                        DokanResult.AccessDenied);
                }
                catch (DirectoryNotFoundException)
                {
                    return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                        DokanResult.PathNotFound);
                }
                catch (Exception ex)
                {
                    var hr = (uint)Marshal.GetHRForException(ex);
                    switch (hr)
                    {
                        case 0x80070020: //Sharing violation
                            return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                                DokanResult.SharingViolation);
                        default:
                            throw;
                    }
                }
            }
            return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                result);
        }

        public void Cleanup(string fileName, IDokanFileInfo info)
        {
#if TRACE
            if (info.Context != null)
                Console.WriteLine(DokanFormat($"{nameof(Cleanup)}('{fileName}', {info} - entering"));
#endif

            (info.Context as FileStream)?.Dispose();
            info.Context = null;

            if (info.DeleteOnClose)
            {
                if (info.IsDirectory)
                {
                    foreach (var path in GetPath(fileName, info.IsDirectory))
                    {
                        Directory.Delete(path);
                    }
                }
                else
                {
                    File.Delete(GetPath(fileName, info.IsDirectory)[0]);
                }
            }
            Trace(nameof(Cleanup), fileName, info, DokanResult.Success);
        }

        public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
        {
            var append = offset == -1;
            if (info.Context == null)
            {
                using (var stream = new FileStream(GetPath(fileName, info.IsDirectory)[0], append ? FileMode.Append : FileMode.Open, System.IO.FileAccess.Write))
                {
                    if (!append) // Offset of -1 is an APPEND: https://docs.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-writefile
                    {
                        stream.Position = offset;
                    }
                    var bytesToCopy = GetNumOfBytesToCopy(buffer.Length, offset, info, stream);
                    stream.Write(buffer, 0, bytesToCopy);
                    bytesWritten = bytesToCopy;
                }
            }
            else
            {
                var stream = info.Context as FileStream;
                lock (stream) //Protect from overlapped write
                {
                    if (append)
                    {
                        if (stream.CanSeek)
                        {
                            stream.Seek(0, SeekOrigin.End);
                        }
                        else
                        {
                            bytesWritten = 0;
                            return Trace(nameof(WriteFile), fileName, info, DokanResult.Error, "out " + bytesWritten,
                                offset.ToString(CultureInfo.InvariantCulture));
                        }
                    }
                    else
                    {
                        stream.Position = offset;
                    }
                    var bytesToCopy = GetNumOfBytesToCopy(buffer.Length, offset, info, stream);
                    stream.Write(buffer, 0, bytesToCopy);
                    bytesWritten = bytesToCopy;
                }
            }
            return Trace(nameof(WriteFile), fileName, info, DokanResult.Success, "out " + bytesWritten.ToString(),
                offset.ToString(CultureInfo.InvariantCulture));
        }

        public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
        {
            try
            {
                ((FileStream)(info.Context)).Flush();
                return Trace(nameof(FlushFileBuffers), fileName, info, DokanResult.Success);
            }
            catch (IOException)
            {
                return Trace(nameof(FlushFileBuffers), fileName, info, DokanResult.DiskFull);
            }
        }

        public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
        {
            try
            {
                // MS-FSCC 2.6 File Attributes : There is no file attribute with the value 0x00000000
                // because a value of 0x00000000 in the FileAttributes field means that the file attributes for this file MUST NOT be changed when setting basic information for the file
                if (attributes != 0)
                    File.SetAttributes(GetPath(fileName, info.IsDirectory)[0], attributes);
                return Trace(nameof(SetFileAttributes), fileName, info, DokanResult.Success, attributes.ToString());
            }
            catch (UnauthorizedAccessException)
            {
                return Trace(nameof(SetFileAttributes), fileName, info, DokanResult.AccessDenied, attributes.ToString());
            }
            catch (FileNotFoundException)
            {
                return Trace(nameof(SetFileAttributes), fileName, info, DokanResult.FileNotFound, attributes.ToString());
            }
            catch (DirectoryNotFoundException)
            {
                return Trace(nameof(SetFileAttributes), fileName, info, DokanResult.PathNotFound, attributes.ToString());
            }
        }

        public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime,
            DateTime? lastWriteTime, IDokanFileInfo info)
        {
            try
            {
                if (info.Context is FileStream stream)
                {
                    var ct = creationTime?.ToFileTime() ?? 0;
                    var lat = lastAccessTime?.ToFileTime() ?? 0;
                    var lwt = lastWriteTime?.ToFileTime() ?? 0;
                    if (NativeMethods.SetFileTime(stream.SafeFileHandle, ref ct, ref lat, ref lwt))
                        return DokanResult.Success;
                    throw Marshal.GetExceptionForHR(Marshal.GetLastWin32Error());
                }

                var filePath = GetPath(fileName, info.IsDirectory);

                if (creationTime.HasValue)
                    File.SetCreationTime(filePath[0], creationTime.Value);

                if (lastAccessTime.HasValue)
                    File.SetLastAccessTime(filePath[0], lastAccessTime.Value);

                if (lastWriteTime.HasValue)
                    File.SetLastWriteTime(filePath[0], lastWriteTime.Value);

                return Trace(nameof(SetFileTime), fileName, info, DokanResult.Success, creationTime, lastAccessTime,
                    lastWriteTime);
            }
            catch (UnauthorizedAccessException)
            {
                return Trace(nameof(SetFileTime), fileName, info, DokanResult.AccessDenied, creationTime, lastAccessTime,
                    lastWriteTime);
            }
            catch (FileNotFoundException)
            {
                return Trace(nameof(SetFileTime), fileName, info, DokanResult.FileNotFound, creationTime, lastAccessTime,
                    lastWriteTime);
            }
        }

        public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
        {
            var filePath = GetPath(fileName, info.IsDirectory);

            if (filePath.Any(fp => Directory.Exists(fp)))
                return Trace(nameof(DeleteFile), fileName, info, DokanResult.AccessDenied);

            if (!File.Exists(filePath[0]))
                return Trace(nameof(DeleteFile), fileName, info, DokanResult.FileNotFound);

            if (filePath.Any(fp => File.GetAttributes(fp).HasFlag(FileAttributes.Directory)))
                return Trace(nameof(DeleteFile), fileName, info, DokanResult.AccessDenied);

            return Trace(nameof(DeleteFile), fileName, info, DokanResult.Success);
            // we just check here if we could delete the file - the true deletion is in Cleanup
        }

        public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
        {
            return Trace(nameof(DeleteDirectory), fileName, info,
                GetPath(fileName, info.IsDirectory).Any(fp => Directory.EnumerateFileSystemEntries(fp).Any())
                    ? DokanResult.DirectoryNotEmpty
                    : DokanResult.Success);
            // if dir is not empty it can't be deleted
        }

        public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
        {
            var oldpath = GetPath(oldName, info.IsDirectory);
            var newpath = GetPath(newName, info.IsDirectory);

            (info.Context as FileStream)?.Dispose();
            info.Context = null;

            var exist = info.IsDirectory ? newpath.Any(np => Directory.Exists(np)) : File.Exists(newpath[0]);

            try
            {
                if (!exist)
                {
                    info.Context = null;
                    if (info.IsDirectory)
                    {
                        foreach (var path in oldpath)
                        {
                            Directory.Move(path, newpath[0]);
                        }
                    }
                    else
                        File.Move(oldpath[0], newpath[0]);
                    return Trace(nameof(MoveFile), oldName, info, DokanResult.Success, newName,
                        replace.ToString(CultureInfo.InvariantCulture));
                }
                else if (replace)
                {
                    info.Context = null;

                    if (info.IsDirectory) //Cannot replace directory destination - See MOVEFILE_REPLACE_EXISTING
                        return Trace(nameof(MoveFile), oldName, info, DokanResult.AccessDenied, newName,
                            replace.ToString(CultureInfo.InvariantCulture));

                    File.Delete(newpath[0]);
                    File.Move(oldpath[0], newpath[0]);
                    return Trace(nameof(MoveFile), oldName, info, DokanResult.Success, newName,
                        replace.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (UnauthorizedAccessException)
            {
                return Trace(nameof(MoveFile), oldName, info, DokanResult.AccessDenied, newName,
                    replace.ToString(CultureInfo.InvariantCulture));
            }
            return Trace(nameof(MoveFile), oldName, info, DokanResult.FileExists, newName,
                replace.ToString(CultureInfo.InvariantCulture));
        }

        public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
        {
            try
            {
                ((FileStream)(info.Context)).SetLength(length);
                return Trace(nameof(SetEndOfFile), fileName, info, DokanResult.Success,
                    length.ToString(CultureInfo.InvariantCulture));
            }
            catch (IOException)
            {
                return Trace(nameof(SetEndOfFile), fileName, info, DokanResult.DiskFull,
                    length.ToString(CultureInfo.InvariantCulture));
            }
        }

        public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
        {
            try
            {
                ((FileStream)(info.Context)).SetLength(length);
                return Trace(nameof(SetAllocationSize), fileName, info, DokanResult.Success,
                    length.ToString(CultureInfo.InvariantCulture));
            }
            catch (IOException)
            {
                return Trace(nameof(SetAllocationSize), fileName, info, DokanResult.DiskFull,
                    length.ToString(CultureInfo.InvariantCulture));
            }
        }

        public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
#if !NETCOREAPP1_0
            try
            {
                ((FileStream)(info.Context)).Lock(offset, length);
                return Trace(nameof(LockFile), fileName, info, DokanResult.Success,
                    offset.ToString(CultureInfo.InvariantCulture), length.ToString(CultureInfo.InvariantCulture));
            }
            catch (IOException)
            {
                return Trace(nameof(LockFile), fileName, info, DokanResult.AccessDenied,
                    offset.ToString(CultureInfo.InvariantCulture), length.ToString(CultureInfo.InvariantCulture));
            }
#else
// .NET Core 1.0 do not have support for FileStream.Lock
            return DokanResult.NotImplemented;
#endif
        }

        public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
#if !NETCOREAPP1_0
            try
            {
                ((FileStream)(info.Context)).Unlock(offset, length);
                return Trace(nameof(UnlockFile), fileName, info, DokanResult.Success,
                    offset.ToString(CultureInfo.InvariantCulture), length.ToString(CultureInfo.InvariantCulture));
            }
            catch (IOException)
            {
                return Trace(nameof(UnlockFile), fileName, info, DokanResult.AccessDenied,
                    offset.ToString(CultureInfo.InvariantCulture), length.ToString(CultureInfo.InvariantCulture));
            }
#else
// .NET Core 1.0 do not have support for FileStream.Unlock
            return DokanResult.NotImplemented;
#endif
        }

        public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections,
            IDokanFileInfo info)
        {
            try
            {
#if NET5_0_OR_GREATER
                if (info.IsDirectory)
                {
                    foreach (var path in GetPath(fileName, info.IsDirectory))
                    {
                        new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
                    }
                }
                else
                {
                    new FileInfo(GetPath(fileName, info.IsDirectory)[0]).SetAccessControl((FileSecurity)security);
                }
#else
                if (info.IsDirectory)
                {
                    Directory.SetAccessControl(GetPath(fileName), (DirectorySecurity)security);
                }
                else
                {
                    File.SetAccessControl(GetPath(fileName), (FileSecurity)security);
                }
#endif
                return Trace(nameof(SetFileSecurity), fileName, info, DokanResult.Success, sections.ToString());
            }
            catch (UnauthorizedAccessException)
            {
                return Trace(nameof(SetFileSecurity), fileName, info, DokanResult.AccessDenied, sections.ToString());
            }
        }

        #endregion Implementation of IDokanOperations
    }
}