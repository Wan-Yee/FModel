using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Relationships;
using Newtonsoft.Json;

namespace FModel.Services.AssetRelationships;

public static class RelationshipManifestWriter
{
    public static void Write(
        string exportDirectory,
        ExportOptions options,
        AssetRelationshipIndex index,
        IReadOnlyDictionary<string, IReadOnlyList<MeshAnimationRelationship>> relationshipsByMesh,
        IReadOnlyList<ExportResult> results)
    {
        Directory.CreateDirectory(exportDirectory);
        var manifestsDirectory = Path.Combine(exportDirectory, "Manifests");
        var indexDirectory = Path.Combine(exportDirectory, "Index");
        Directory.CreateDirectory(manifestsDirectory);
        Directory.CreateDirectory(indexDirectory);

        var exportedFiles = BuildExportedFileMap(exportDirectory, results);

        if (options.ExportMeshProfiles || options.ExportRelationshipManifest)
        {
            WriteJson(Path.Combine(indexDirectory, "assets.json"), new
            {
                schema_version = "1.0",
                skeletal_meshes = index.SkeletalMeshes.Select(ToJsonMeshProfile).ToArray(),
                animations = index.Animations.Select(ToJsonAnimationMetadata).ToArray()
            });
        }

        if (options.ExportRelationshipManifest)
        {
            WriteJson(Path.Combine(indexDirectory, "references.json"), new
            {
                schema_version = "1.0",
                edges = index.ReferenceGraph.Edges.Select(edge => new
                {
                    source_asset = edge.SourceAsset,
                    target_asset = edge.TargetAsset,
                    reference_type = edge.ReferenceType.ToString(),
                    evidence_source = edge.EvidenceSource,
                    property_path = edge.PropertyPath,
                    is_soft_reference = edge.IsSoftReference
                }).ToArray()
            });

            WriteJson(Path.Combine(indexDirectory, "export_index.json"), new
            {
                schema_version = "1.0",
                exports = exportedFiles.Select(kvp => new
                {
                    asset_path = kvp.Key,
                    files = kvp.Value
                }).ToArray()
            });

            foreach (var (meshPath, relationships) in relationshipsByMesh)
            {
                if (!index.SkeletalMeshByPath.TryGetValue(meshPath, out var mesh)) continue;
                var manifestPath = Path.Combine(manifestsDirectory, $"{SanitizeFileName(mesh.AssetName)}.{ShortHash(mesh.AssetPath)}.animmap.json");
                WriteJson(manifestPath, BuildMeshManifest(mesh, relationships, index, exportedFiles));
            }
        }
    }

    private static object BuildMeshManifest(
        SkeletalMeshProfile mesh,
        IReadOnlyList<MeshAnimationRelationship> relationships,
        AssetRelationshipIndex index,
        IReadOnlyDictionary<string, string[]> exportedFiles)
    {
        var records = relationships.Select(relationship =>
        {
            index.AnimationByPath.TryGetValue(relationship.AnimationAssetPath, out var animation);
            exportedFiles.TryGetValue(relationship.AnimationAssetPath, out var files);
            return new
            {
                animation_asset_path = relationship.AnimationAssetPath,
                animation_name = animation?.AssetName ?? AssetReferenceGraph.GetAssetName(relationship.AnimationAssetPath),
                skeleton_asset_path = animation?.SkeletonPath,
                exported_psa_path = files?.FirstOrDefault(),
                relationship_level = ToManifestLevel(relationship.RelationshipLevel),
                evidence = relationship.Evidence.Select(e => new
                {
                    type = e.Type,
                    source = e.Source,
                    target = e.Target,
                    detail = e.Detail,
                    is_inference = e.IsInference
                }).ToArray(),
                compatibility_status = relationship.CompatibilityStatus.ToString()
            };
        }).ToArray();

        return new
        {
            schema_version = "1.0",
            mesh_asset_path = mesh.AssetPath,
            mesh_name = mesh.AssetName,
            skeleton_asset_path = mesh.SkeletonPath,
            direct_references = records.Where(x => x.relationship_level == "DIRECT_REFERENCE").ToArray(),
            strong_candidates = records.Where(x => x.relationship_level == "STRONG_CANDIDATE").ToArray(),
            weak_candidates = records.Where(x => x.relationship_level == "WEAK_CANDIDATE").ToArray(),
            skeleton_only = records.Where(x => x.relationship_level == "SKELETON_ONLY").ToArray(),
            unresolved = records.Where(x => x.relationship_level == "UNRESOLVED").ToArray(),
            index_version = "1.0"
        };
    }

    private static IReadOnlyDictionary<string, string[]> BuildExportedFileMap(string exportDirectory, IReadOnlyList<ExportResult> results)
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
        {
            if (!result.Success || result.DiskFilePaths == null) continue;
            map[result.ObjectPath] = result.DiskFilePaths
                .Select(path => Path.GetRelativePath(exportDirectory, path).Replace('\\', '/'))
                .ToArray();
        }
        return map;
    }

    private static object ToJsonMeshProfile(SkeletalMeshProfile mesh) => new
    {
        asset_path = mesh.AssetPath,
        asset_name = mesh.AssetName,
        asset_type = mesh.AssetType,
        skeleton_path = mesh.SkeletonPath,
        mesh_role = mesh.MeshRole,
        mesh_role_inferred = mesh.MeshRoleInferred,
        bone_count = mesh.BoneCount,
        lod_count = mesh.LodCount,
        references = mesh.References,
        source_package = mesh.SourcePackage,
        extraction_status = mesh.ExtractionStatus,
        reference_skeleton = mesh.ReferenceSkeleton.Select(bone => new { name = bone.Name, parent_index = bone.ParentIndex }).ToArray(),
        weighted_bone_profile = mesh.WeightedBoneProfile == null ? null : new
        {
            profile_status = mesh.WeightedBoneProfile.ProfileStatus,
            unavailable_reason = mesh.WeightedBoneProfile.UnavailableReason,
            skeleton_bones = mesh.WeightedBoneProfile.SkeletonBones,
            effective_bones = mesh.WeightedBoneProfile.EffectiveBones
        }
    };

    private static object ToJsonAnimationMetadata(AnimationMetadata animation) => new
    {
        asset_path = animation.AssetPath,
        asset_name = animation.AssetName,
        asset_type = animation.AssetType,
        skeleton_path = animation.SkeletonPath,
        duration = animation.Duration,
        frame_count = animation.FrameCount,
        retarget_source = animation.RetargetSource,
        retarget_source_asset = animation.RetargetSourceAsset,
        references = animation.References,
        referenced_by = animation.ReferencedBy,
        track_bone_names = animation.TrackBoneNames,
        extraction_status = animation.ExtractionStatus
    };

    private static string ToManifestLevel(MeshAnimationRelationshipLevel level) => level switch
    {
        MeshAnimationRelationshipLevel.DirectReference => "DIRECT_REFERENCE",
        MeshAnimationRelationshipLevel.StrongCandidate => "STRONG_CANDIDATE",
        MeshAnimationRelationshipLevel.WeakCandidate => "WEAK_CANDIDATE",
        MeshAnimationRelationshipLevel.SkeletonOnly => "SKELETON_ONLY",
        _ => "UNRESOLVED"
    };

    private static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonConvert.SerializeObject(value, Formatting.Indented), Encoding.UTF8);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name.Replace('/', '_').Replace('\\', '_');
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}
