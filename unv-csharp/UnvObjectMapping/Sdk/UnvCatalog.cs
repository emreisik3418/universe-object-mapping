using System;
using System.Collections.Generic;
using System.Linq;
using CrystalDecisions.Enterprise;
using UnvObjectMapping.Model;

namespace UnvObjectMapping.Sdk
{
    /// <summary>CMS'teki UNV universe'leri klasör yollarıyla birlikte listeler (toplam 2 sorgu).</summary>
    public static class UnvCatalog
    {
        /// <summary>CMS'te universe kök klasörünün sabit ID'si ("Universes").</summary>
        private const int UniverseRootId = 95;

        private const string FolderQuery =
            "SELECT TOP 1000000 SI_ID, SI_NAME, SI_PARENTID FROM CI_APPOBJECTS WHERE SI_KIND = 'Folder'";

        // SI_KIND = 'Universe' -> UNV (UNX'ler 'DSL.Universe' olarak ayrı tutulur)
        private const string UnvQuery =
            "SELECT TOP 1000000 SI_ID, SI_CUID, SI_NAME, SI_PARENTID FROM CI_APPOBJECTS WHERE SI_KIND = 'Universe'";

        public static List<UniverseInfo> ListUnv(InfoStore infoStore)
        {
            // 1) Tüm universe klasörleri: ID -> (ad, üst ID)
            var folders = new Dictionary<int, KeyValuePair<string, int>>();
            InfoObjects folderObjects = infoStore.Query(FolderQuery);
            foreach (InfoObject f in folderObjects)
                folders[f.ID] = new KeyValuePair<string, int>(f.Title, f.ParentID);

            // 2) Tüm UNV'ler, yolları bellekte oluşturulur
            var result = new List<UniverseInfo>();
            InfoObjects unvObjects = infoStore.Query(UnvQuery);
            foreach (InfoObject u in unvObjects)
            {
                result.Add(new UniverseInfo
                {
                    Id = u.ID,
                    Cuid = u.CUID,
                    Name = u.Title,
                    FolderPath = FolderPath(u.ParentID, folders)
                });
            }

            return result.OrderBy(x => x.FolderPath, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                         .ToList();
        }

        private static string FolderPath(int folderId, Dictionary<int, KeyValuePair<string, int>> folders)
        {
            var parts = new List<string>();
            int current = folderId;
            int guard = 0;   // bozuk veride sonsuz döngüye girmesin
            while (current != UniverseRootId && folders.ContainsKey(current) && guard++ < 100)
            {
                var f = folders[current];
                if (string.Equals(f.Key, "Universes", StringComparison.OrdinalIgnoreCase))
                    break;   // kök klasöre ulaştık, yola ekleme
                parts.Insert(0, f.Key);
                current = f.Value;
            }
            return string.Join("\\", parts);
        }
    }
}
