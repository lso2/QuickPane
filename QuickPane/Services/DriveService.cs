using System;
using System.Collections.Generic;
using System.IO;
using QuickPane.Services;

namespace QuickPane.Services
{
    public sealed class DriveItem
    {
        public string Letter { get; set; }      // "C:"
        public string RootPath { get; set; }     // "C:\"
        public string Label { get; set; }        // volume label or a sensible default
        public bool IsNetwork { get; set; }
        public bool IsRemovable { get; set; }
        public bool Ready { get; set; }
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }

        public double UsedFraction
        {
            get
            {
                if (TotalBytes <= 0) return 0;
                var used = (double)(TotalBytes - FreeBytes) / TotalBytes;
                return used < 0 ? 0 : (used > 1 ? 1 : used);
            }
        }
    }

    /// <summary>
    /// Snapshots fixed, removable, and network drives for the Computer section. IsReady, volume
    /// labels, and sizes are queried in full only on the background worker: a mapped network drive
    /// whose server is gone blocks those calls for many seconds, and this used to run on the UI
    /// thread on every pane rebuild. The synchronous path returns the cached snapshot (details for
    /// fixed local drives only on the very first call) and kicks a refresh.
    /// </summary>
    public sealed class DriveService
    {
        /// <summary>Raised on the UI thread when a background refresh replaced the snapshot.</summary>
        public event Action Refreshed;

        private volatile List<DriveItem> _cache;
        private int _refreshing;

        public List<DriveItem> GetDrives()
        {
            var c = _cache;
            if (c == null)
            {
                c = Snapshot(fullDetail: false); // fixed drives only; instant
                _cache = c;
                RefreshAsync();
            }
            return c;
        }

        /// <summary>Re-query everything (including slow volumes) on the worker.</summary>
        public void RefreshAsync()
        {
            if (System.Threading.Interlocked.Exchange(ref _refreshing, 1) == 1) return;
            WorkQueue.Post(() =>
            {
                try
                {
                    var list = Snapshot(fullDetail: true);
                    _cache = list;
                    WorkQueue.PostUI(() => { Refreshed?.Invoke(); });
                }
                finally { System.Threading.Interlocked.Exchange(ref _refreshing, 0); }
            });
        }

        private static List<DriveItem> Snapshot(bool fullDetail)
        {
            var list = new List<DriveItem>();
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch (Exception ex) { Log.Error("GetDrives failed", ex); return list; }

            foreach (var d in drives)
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed &&
                        d.DriveType != DriveType.Removable &&
                        d.DriveType != DriveType.Network)
                        continue;

                    var item = new DriveItem
                    {
                        Letter = d.Name.TrimEnd('\\'),
                        RootPath = d.Name,
                        IsNetwork = d.DriveType == DriveType.Network,
                        IsRemovable = d.DriveType == DriveType.Removable
                    };

                    // IsReady / labels / sizes can block on non-fixed volumes, so the quick pass
                    // leaves them at optimistic defaults and the worker fills them in.
                    bool queryDetail = fullDetail || d.DriveType == DriveType.Fixed;
                    if (queryDetail && d.IsReady)
                    {
                        item.Ready = true;
                        item.Label = string.IsNullOrWhiteSpace(d.VolumeLabel)
                            ? DefaultLabel(d.DriveType)
                            : d.VolumeLabel;
                        item.TotalBytes = d.TotalSize;
                        item.FreeBytes = d.TotalFreeSpace;
                    }
                    else
                    {
                        item.Ready = !queryDetail; // assume alive until the worker says otherwise
                        item.Label = DefaultLabel(d.DriveType);
                    }

                    list.Add(item);
                }
                catch (Exception ex)
                {
                    Log.Error("drive read failed for " + d.Name, ex);
                }
            }
            return list;
        }

        private static string DefaultLabel(DriveType type)
        {
            switch (type)
            {
                case DriveType.Removable: return "Removable Disk";
                case DriveType.Network: return "Network Drive";
                default: return "Local Disk";
            }
        }
    }
}
