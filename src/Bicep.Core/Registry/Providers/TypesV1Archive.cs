// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Azure.Bicep.Types.Serialization;

namespace Bicep.Core.Registry.Providers;

public static class TypesV1Archive
{
    public static async Task<BinaryData> GenerateProviderTarStream(IFileSystem fileSystem, string indexJsonPath)
    {
        using var stream = new MemoryStream();

        using (var gzStream = new GZipStream(stream, CompressionMode.Compress, leaveOpen: true))
        {
            using var tarWriter = new TarWriter(gzStream, leaveOpen: true);

            var indexJson = await fileSystem.File.ReadAllTextAsync(indexJsonPath);
            await AddFileToTar(tarWriter, "index.json", indexJson);

            var indexJsonParentPath = Path.GetDirectoryName(indexJsonPath);
            var uniqueTypePaths = GetAllUniqueTypePaths(indexJsonPath, fileSystem);

            foreach (var relativePath in uniqueTypePaths)
            {
                var absolutePath = Path.Combine(indexJsonParentPath!, relativePath);
                var typesJson = await fileSystem.File.ReadAllTextAsync(absolutePath);
                await AddFileToTar(tarWriter, relativePath, typesJson);
            }
        }

        stream.Seek(0, SeekOrigin.Begin);

        return BinaryData.FromStream(stream);
    }

    private static async Task AddFileToTar(TarWriter tarWriter, string archivePath, string contents)
    {
        var tarEntry = new PaxTarEntry(TarEntryType.RegularFile, archivePath)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(contents))
        };

        await tarWriter.WriteEntryAsync(tarEntry);
    }

    private static IEnumerable<string> GetAllUniqueTypePaths(string pathToIndex, IFileSystem fileSystem)
    {
        using var indexStream = fileSystem.FileStream.New(pathToIndex, FileMode.Open, FileAccess.Read);

        var index = TypeSerializer.DeserializeIndex(indexStream);

        return index.Resources.Values.Select(x => x.RelativePath).Distinct();
    }
}

