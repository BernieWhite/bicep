// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bicep.Core.Diagnostics;
using Bicep.Core.Exceptions;
using Bicep.Core.Workspaces;

namespace Bicep.Core.SourceCode
{
    public class SourceNotAvailableException : Exception
    {
        public SourceNotAvailableException()
            : base("(Experimental) No source code is available for this module")
        { }
    }

    // Contains the individual source code files for a Bicep file and all of its dependencies.
    public partial class SourceArchive // Partial required for serialization
    {
        // Attributes of this archive instance
        #region Attributes of this archive instance

        private ArchiveMetadata InstanceMetadata { get; init; }

        public ImmutableArray<SourceFileInfo> SourceFiles { get; init; }

        public string EntrypointRelativePath => InstanceMetadata.EntryPoint;

        // The version of Bicep which created this deserialized archive instance.
        public string? BicepVersion => InstanceMetadata.BicepVersion;
        public string FriendlyBicepVersion => InstanceMetadata.BicepVersion ?? "unknown";

        // The version of the metadata file format used by this archive instance.
        public int MetadataVersion => InstanceMetadata.MetadataVersion;

        public IReadOnlyDictionary<string, SourceCodeDocumentPathLink[]> DocumentLinks => InstanceMetadata.DocumentLinks ?? new Dictionary<string, SourceCodeDocumentPathLink[]>();

        #endregion

        #region Constants

        public const string SourceKind_Bicep = "bicep";
        public const string SourceKind_ArmTemplate = "armTemplate";
        public const string SourceKind_TemplateSpec = "templateSpec";

        private const string MetadataFileName = "__metadata.json";

        // NOTE: Only change this value if there is a breaking change such that old versions of Bicep should fail on reading new source archives
        public const int CurrentMetadataVersion = 0;
        private static readonly string CurrentBicepVersion = ThisAssembly.AssemblyVersion;

        #endregion

        #region Serialization

        public partial record SourceFileInfo(
            // Note: Path is also used as the key for source file retrieval
            string Path,        // the location, relative to the main.bicep file's folder, for the file that will be shown to the end user (required in all Bicep versions)
            string ArchivePath, // the location (relative to root) of where the file is stored in the archive (munged from Path, e.g. in case Path starts with "../")
            string Kind,         // kind of source
            string Contents
        );

        [JsonSerializable(typeof(ArchiveMetadata))]
        [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
        private partial class MetadataSerializationContext : JsonSerializerContext { }

        // Metadata for the entire archive (stored in __metadata.json file in archive)
        [JsonSerializable(typeof(ArchiveMetadata))]
        private record ArchiveMetadata(
            int MetadataVersion,
            string EntryPoint, // Path of the entrypoint file
            IEnumerable<SourceFileInfoEntry> SourceFiles,
            string? BicepVersion = null,
            IReadOnlyDictionary<string, SourceCodeDocumentPathLink[]>? DocumentLinks = null // Maps source file path -> array of document links inside that file
        );

        [JsonSerializable(typeof(SourceFileInfoEntry))]
        private partial record SourceFileInfoEntry(
            // IF ADDING TO THIS: Remember both forwards and backwards compatibility.
            // E.g., previous versions must be able to deal with unrecognized source kinds.
            // (but see CurrentMetadataVersion for breaking changes)
            string Path,        // the location, relative to the main.bicep file's folder, for the file that will be shown to the end user (required in all Bicep versions)
            string ArchivePath, // the location (relative to root) of where the file is stored in the archive
            string Kind         // kind of source
        );

        #endregion

        public static ResultWithException<SourceArchive> UnpackFromStream(Stream stream)
        {
            var archive = new SourceArchive(stream);
            if (archive.GetRequiredBicepVersionMessage() is string message)
            {
                return new(new Exception(message));
            }
            else
            {
                return new(archive);
            }
        }

        /// <summary>
        /// Bundles all the sources from a compilation group (thus source for a bicep file and all its dependencies
        /// in JSON form) into an archive (as a stream)
        /// </summary>
        /// <returns>A .tar.gz file as a binary stream</returns>
        public static Stream PackSourcesIntoStream(SourceFileGrouping sourceFileGrouping)
        {
            var documentLinks = SourceCodeDocumentLinkHelper.GetAllModuleDocumentLinks(sourceFileGrouping);
            return PackSourcesIntoStream(sourceFileGrouping.EntryFileUri, documentLinks, sourceFileGrouping.SourceFiles.ToArray());
        }

        public static Stream PackSourcesIntoStream(Uri entrypointFileUri, params ISourceFile[] sourceFiles)
        {
            return PackSourcesIntoStream(entrypointFileUri, documentLinks: null, sourceFiles);
        }

        // TODO: Toughen this up to handle conflicting paths, ".." paths, etc.
        public static Stream PackSourcesIntoStream(Uri entrypointFileUri, IReadOnlyDictionary<Uri, SourceCodeDocumentUriLink[]>? documentLinks, params ISourceFile[] sourceFiles)
        {
            var baseFolderBuilder = new UriBuilder(entrypointFileUri)
            {
                Path = string.Join("", entrypointFileUri.Segments.SkipLast(1))
            };
            var baseFolderUri = baseFolderBuilder.Uri;

            var stream = new MemoryStream();
            using (var gz = new GZipStream(stream, CompressionMode.Compress, leaveOpen: true))
            {
                using (var tarWriter = new TarWriter(gz, leaveOpen: true))
                {
                    var filesMetadata = new List<SourceFileInfoEntry>();
                    string? entryPointPath = null;

                    foreach (var file in sourceFiles)
                    {
                        string source = file.GetOriginalSource();
                        string kind = file switch
                        {
                            BicepFile bicepFile => SourceKind_Bicep,
                            ArmTemplateFile armTemplateFile => SourceKind_ArmTemplate,
                            TemplateSpecFile => SourceKind_TemplateSpec,
                            _ => throw new ArgumentException($"Unexpected source file type {file.GetType().Name}"),
                        };

                        var (location, archivePath) = CalculateFilePathsFromUri(file.FileUri);
                        WriteNewFileEntry(tarWriter, archivePath, source);
                        filesMetadata.Add(new SourceFileInfoEntry(location, archivePath, kind));

                        if (file.FileUri == entrypointFileUri)
                        {
                            if (entryPointPath is not null)
                            {
                                throw new ArgumentException($"{nameof(SourceArchive)}.{nameof(PackSourcesIntoStream)}: Multiple source files with the entrypoint \"{entrypointFileUri.AbsoluteUri}\" were passed in.");
                            }

                            entryPointPath = location;
                        }
                    }

                    if (entryPointPath is null)
                    {
                        throw new ArgumentException($"{nameof(SourceArchive)}.{nameof(PackSourcesIntoStream)}: No source file with entrypoint \"{entrypointFileUri.AbsoluteUri}\" was passed in.");
                    }

                    // Add the metadata file
                    var metadataContents = CreateMetadataFileContents(baseFolderUri, entryPointPath, filesMetadata, documentLinks);
                    WriteNewFileEntry(tarWriter, MetadataFileName, metadataContents);
                }
            }

            stream.Seek(0, SeekOrigin.Begin);
            return stream;

            (string location, string archivePath) CalculateFilePathsFromUri(Uri uri)
            {
                var relativeLocation = CalculateRelativeFilePath(baseFolderUri, uri);
                return (relativeLocation, relativeLocation);
            }
        }

        public SourceFileInfo FindExpectedSourceFile(string path)
        {
            if (this.SourceFiles.FirstOrDefault(f => 0 == StringComparer.Ordinal.Compare(f.Path, path)) is { } sourceFile)
            {
                return sourceFile;
            }

            throw new Exception($"Unable to find source file \"{path}\" in the source archive");
        }

        private string? GetRequiredBicepVersionMessage()
        {
            if (MetadataVersion < CurrentMetadataVersion)
            {
                return $"This source code was published with an older, incompatible version of Bicep ({FriendlyBicepVersion}). You are using version {ThisAssembly.AssemblyVersion}.";
            }

            if (MetadataVersion > CurrentMetadataVersion)
            {
                return $"This source code was published with a newer, incompatible version of Bicep ({FriendlyBicepVersion}). You are using version {ThisAssembly.AssemblyVersion}. You need a newer version in order to view the module source.";
            }

            return null;
        }

        private static string CalculateRelativeFilePath(Uri baseFolderUri, Uri uri)
        {
            Uri relativeUri = baseFolderUri.MakeRelativeUri(uri);
            var relativeLocation = Uri.UnescapeDataString(relativeUri.OriginalString);
            return relativeLocation;
        }

        private SourceArchive(Stream stream)
        {
            var filesBuilder = ImmutableDictionary.CreateBuilder<string, string>();

            var gz = new GZipStream(stream, CompressionMode.Decompress);
            using var tarReader = new TarReader(gz);

            while (tarReader.GetNextEntry() is { } entry)
            {
                string contents = entry.DataStream is null ? string.Empty : new StreamReader(entry.DataStream, Encoding.UTF8).ReadToEnd();
                filesBuilder.Add(entry.Name, contents);
            }

            var dictionary = filesBuilder.ToImmutableDictionary();

            var metadataJson = dictionary[MetadataFileName]
                ?? throw new BicepException("Incorrectly formatted source file: No {MetadataArchivedFileName} entry");
            var metadata = JsonSerializer.Deserialize(metadataJson, MetadataSerializationContext.Default.ArchiveMetadata)
                ?? throw new BicepException("Source archive has invalid metadata entry");

            var infos = new List<SourceFileInfo>();
            foreach (var info in metadata.SourceFiles.OrderBy(e => e.Path).ThenBy(e => e.ArchivePath))
            {
                var contents = dictionary[info.ArchivePath]
                    ?? throw new BicepException("Incorrectly formatted source file: File entry not found: \"{info.ArchivePath}\"");
                infos.Add(new SourceFileInfo(info.Path, info.ArchivePath, info.Kind, contents));
            }

            this.InstanceMetadata = metadata;
            this.SourceFiles = infos.ToImmutableArray();
        }

        private static string CreateMetadataFileContents(
            Uri baseFolderUri,
            string entrypointPath,
            IEnumerable<SourceFileInfoEntry> files,
            IReadOnlyDictionary<Uri, SourceCodeDocumentUriLink[]>? documentLinks
        )
        {
            // Add the __metadata.json file
            var metadata = new ArchiveMetadata(CurrentMetadataVersion, entrypointPath, files, CurrentBicepVersion, UriDocumentLinksToPathBasedLinks(baseFolderUri, documentLinks));
            return JsonSerializer.Serialize(metadata, MetadataSerializationContext.Default.ArchiveMetadata);
        }

        private static IReadOnlyDictionary<string, SourceCodeDocumentPathLink[]>? UriDocumentLinksToPathBasedLinks(
            Uri baseFolderUri,
            IReadOnlyDictionary<Uri, SourceCodeDocumentUriLink[]>? uriBasedDocumentLinks
        )
        {
            return uriBasedDocumentLinks?.Select(
                x => new KeyValuePair<string, SourceCodeDocumentPathLink[]>(
                    CalculateRelativeFilePath(baseFolderUri, x.Key),
                    x.Value.Select(link => DocumentPathLinkFromUriLink(baseFolderUri, link)).ToArray()
                )).ToImmutableDictionary();
        }

        private static SourceCodeDocumentPathLink DocumentPathLinkFromUriLink(Uri baseFolderUri, SourceCodeDocumentUriLink uriBasedLink)
        {
            return new SourceCodeDocumentPathLink(
                uriBasedLink.Range,
                CalculateRelativeFilePath(baseFolderUri, uriBasedLink.Target));
        }

        private static void WriteNewFileEntry(TarWriter tarWriter, string archivePath, string contents)
        {
            var tarEntry = new PaxTarEntry(TarEntryType.RegularFile, archivePath);
            tarEntry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(contents));
            tarWriter.WriteEntry(tarEntry);
        }
    }
}
