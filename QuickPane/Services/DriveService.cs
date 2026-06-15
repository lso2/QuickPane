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

    /// <summary>Snapshots fixed, removable, and network drives for the Computer section.</summary>
    public sealed class DriveService
    {
        public List<DriveItem> GetDrives()
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
                        IsRemovable = d.DriveType == DriveType.Removable,
                        Ready = d.IsReady
                    };

                    if (d.IsReady)
                    {
                        item.Label = string.IsNullOrWhiteSpace(d.VolumeLabel)
                            ? DefaultLabel(d.DriveType)
                            : d.VolumeLabel;
                        item.TotalBytes = d.TotalSize;
                        item.FreeBytes = d.TotalFreeSpace;
                    }
                    else
                    {
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
