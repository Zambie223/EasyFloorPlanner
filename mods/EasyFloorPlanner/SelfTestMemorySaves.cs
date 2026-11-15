#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kitchen;
using Platforms;

namespace EasyFloorPlanner
{
    /// <summary>
    /// DEBUG BUILDS ONLY. Lets the self-test exercise the game's real save and reload path without
    /// touching disk.
    ///
    /// Persistence.FullWorld (the per-run world save) is a public static field. We replace it with
    /// a FullWorldSaveSystem whose SaveStorage instances sit on an in-memory file system.
    /// SaveStorage keeps saves in its own in-memory list, and FullWorldSaveSystem.Load reads from
    /// that list, so the full Save → transition → CreateSceneAutosaveLoad → Load flow runs
    /// unchanged. SaveGuard lets writes through only for these instances; every real storage
    /// stays blocked.
    /// </summary>
    internal static class SelfTestMemorySaves
    {
        private static SaveStorage<Nothing>[] _storages;

        public static bool Installed => _storages != null;

        public static void Install()
        {
            if (Installed)
            {
                return;
            }
            var fs = new MemoryFileSystem();
            _storages = Enumerable.Range(1, 5).Select(i => new SaveStorage<Nothing>(fs, "EasyFloorPlannerSelfTest/" + i)).ToArray();
            Persistence.FullWorld = new FullWorldSaveSystem(_storages, Persistence.Progress);
            ModMain.Log("[SELFTEST] full-world saves now go to memory only.");
        }

        public static bool Owns(object storage) => _storages != null && Array.IndexOf(_storages, storage) >= 0;

        public static int SaveCount => _storages?.Sum(s => s.Get().Count) ?? 0;

        private sealed class MemoryFileSystem : IAsyncFileSystem
        {
            private readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>();

            public event Action<string> OnFileSystemChange { add { } remove { } }
            public event Action OnClearFileSystemChanges { add { } remove { } }

            public Task<byte[]> ReadAllBytes(string path) =>
                Task.FromResult(_files.TryGetValue(path, out byte[] b) ? b : throw new System.IO.FileNotFoundException(path));

            public Task WriteAllBytes(string path, byte[] bytes)
            {
                _files[path] = bytes;
                return Task.CompletedTask;
            }

            public Task CreateDirectory(string directory) => Task.CompletedTask;

            public Task DeleteFile(string path)
            {
                _files.Remove(path);
                return Task.CompletedTask;
            }

            public Task<IEnumerable<FileReference>> GetFiles(string path, string ext, bool filter_empty) =>
                Task.FromResult(Enumerable.Empty<FileReference>());
        }
    }
}
#endif
