using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CUE4Parse.UE4.Assets;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Component.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Relationships;
using Serilog;

namespace FModel.Services.AssetRelationships;

public sealed class AssetRelationshipIndexer(IFileProvider provider)
{
    private const int MaxPropertyDepth = 12;
    private static readonly ILogger Log = Serilog.Log.ForContext<AssetRelationshipIndexer>();

    public AssetRelationshipIndex Build(CancellationToken cancellationToken, bool exportMeshProfiles)
    {
        var index = new AssetRelationshipIndex();
        var meshes = new Dictionary<string, SkeletalMeshProfile>(StringComparer.OrdinalIgnoreCase);
        var animations = new Dictionary<string, AnimationMetadata>(StringComparer.OrdinalIgnoreCase);

        foreach (var gameFile in provider.Files.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (gameFile == null || !gameFile.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                if (!provider.TryLoadPackage(gameFile, out var package)) continue;

                for (var i = 0; i < package.ExportMapLength; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var resolved = new FPackageIndex(package, i + 1).ResolvedObject;
                    if (resolved == null || !ShouldLoadExport(resolved)) continue;

                    if (!resolved.TryLoad(out var export)) continue;
                    var sourcePath = NormalizeObjectPath(export.GetPathName());
                    if (string.IsNullOrEmpty(sourcePath)) continue;

                    index.ReferenceGraph.AddNode(sourcePath, export.ExportType);
                    CollectObjectReferences(index.ReferenceGraph, sourcePath, export);

                    switch (export)
                    {
                        case USkinnedAsset skinnedAsset:
                        {
                            if (!meshes.ContainsKey(sourcePath))
                            {
                                meshes[sourcePath] = ExtractMeshProfile(skinnedAsset, sourcePath, exportMeshProfiles);
                            }
                            AddSkeletonEdge(index.ReferenceGraph, sourcePath, skinnedAsset.Skeleton);
                            break;
                        }
                        case UAnimationAsset animationAsset:
                        {
                            if (!animations.ContainsKey(sourcePath))
                            {
                                animations[sourcePath] = ExtractAnimationMetadata(animationAsset, sourcePath);
                            }
                            AddSkeletonEdge(index.ReferenceGraph, sourcePath, animationAsset.Skeleton);
                            AddAnimationSegmentEdges(index.ReferenceGraph, sourcePath, animationAsset);
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to index package {Path}", gameFile.Path);
            }
        }

        foreach (var mesh in meshes.Values)
        {
            mesh.References.AddRange(index.ReferenceGraph.GetOutgoing(mesh.AssetPath)
                .Select(edge => edge.TargetAsset)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            index.SkeletalMeshes.Add(mesh);
        }

        foreach (var animation in animations.Values)
        {
            animation.References.AddRange(index.ReferenceGraph.GetOutgoing(animation.AssetPath)
                .Select(edge => edge.TargetAsset)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            animation.ReferencedBy.AddRange(index.ReferenceGraph.GetIncoming(animation.AssetPath)
                .Select(edge => edge.SourceAsset)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            index.Animations.Add(animation);
        }

        return index;
    }

    private static bool ShouldLoadExport(ResolvedObject resolved)
    {
        var className = resolved.Class?.Name.Text ?? string.Empty;
        var exportName = resolved.Name.Text ?? string.Empty;
        return ContainsAny(className, "SkeletalMesh", "Skinned", "Anim", "Blueprint", "GeneratedClass", "Character", "Actor", "Component", "DataAsset") ||
               ContainsAny(exportName, "AnimBP", "ABP_", "BP_", "CH_", "SK_", "AN_", "AM_");
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static SkeletalMeshProfile ExtractMeshProfile(USkinnedAsset mesh, string assetPath, bool exportMeshProfiles)
    {
        var skeletonPath = TryResolvePath(mesh.Skeleton);
        var profile = new SkeletalMeshProfile
        {
            AssetPath = assetPath,
            AssetName = mesh.Name,
            AssetType = mesh.ExportType,
            SkeletonPath = skeletonPath,
            MeshRole = InferMeshRole(mesh.Name),
            MeshRoleInferred = InferMeshRole(mesh.Name) != "unknown",
            BoneCount = mesh.ReferenceSkeleton?.FinalRefBoneInfo?.Length,
            LodCount = mesh.LODModels?.Length,
            SourcePackage = mesh.Owner?.Name,
            ExtractionStatus = "complete"
        };

        if (mesh.ReferenceSkeleton?.FinalRefBoneInfo != null)
        {
            foreach (var bone in mesh.ReferenceSkeleton.FinalRefBoneInfo)
            {
                profile.ReferenceSkeleton.Add(new ReferenceBoneInfo
                {
                    Name = bone.Name.Text,
                    ParentIndex = bone.ParentIndex
                });
            }
        }

        if (exportMeshProfiles)
        {
            profile.WeightedBoneProfile = TryBuildWeightedBoneProfile(mesh);
        }

        return profile;
    }

    private static string InferMeshRole(string name)
    {
        if (name.Contains("facial", StringComparison.OrdinalIgnoreCase) || name.Contains("face", StringComparison.OrdinalIgnoreCase)) return "facial";
        if (name.Contains("body", StringComparison.OrdinalIgnoreCase)) return "body";
        if (name.Contains("hair", StringComparison.OrdinalIgnoreCase)) return "hair";
        if (name.Contains("cloth", StringComparison.OrdinalIgnoreCase) || name.Contains("costume", StringComparison.OrdinalIgnoreCase)) return "clothing";
        return "unknown";
    }

    private static AnimationMetadata ExtractAnimationMetadata(UAnimationAsset animation, string assetPath)
    {
        var metadata = new AnimationMetadata
        {
            AssetPath = assetPath,
            AssetName = animation.Name,
            AssetType = animation.ExportType,
            SkeletonPath = TryResolvePath(animation.Skeleton),
            ExtractionStatus = "complete"
        };

        if (animation is UAnimSequenceBase sequenceBase)
        {
            metadata.Duration = sequenceBase.SequenceLength;
        }

        if (animation is UAnimSequence sequence)
        {
            metadata.FrameCount = sequence.NumFrames > 0 ? sequence.NumFrames : null;
            metadata.RetargetSource = sequence.RetargetSource.IsNone ? null : sequence.RetargetSource.Text;
            if (sequence.CompressedTrackToSkeletonMapTable is { Length: > 0 })
            {
                metadata.TrackBoneNames.AddRange(sequence.CompressedTrackToSkeletonMapTable.Select(x => x.BoneTreeIndex.ToString()));
            }
        }

        return metadata;
    }

    private static void AddSkeletonEdge(AssetReferenceGraph graph, string sourcePath, FPackageIndex? skeleton)
    {
        var skeletonPath = TryResolvePath(skeleton);
        if (string.IsNullOrEmpty(skeletonPath)) return;
        graph.AddEdge(new AssetEdge
        {
            SourceAsset = sourcePath,
            TargetAsset = skeletonPath,
            ReferenceType = AssetReferenceType.Skeleton,
            EvidenceSource = "Skeleton property",
            PropertyPath = "Skeleton"
        });
    }

    private static void AddAnimationSegmentEdges(AssetReferenceGraph graph, string sourcePath, UAnimationAsset animation)
    {
        switch (animation)
        {
            case UAnimMontage montage:
                foreach (var segment in montage.SlotAnimTracks.SelectMany(track => track.AnimTrack.AnimSegments))
                {
                    AddPackageIndexEdge(graph, sourcePath, segment.AnimReference, AssetReferenceType.AnimationSegment, "SlotAnimTracks.AnimTrack.AnimSegments.AnimReference");
                }
                break;
            case UAnimComposite composite:
                foreach (var segment in composite.AnimationTrack.AnimSegments)
                {
                    AddPackageIndexEdge(graph, sourcePath, segment.AnimReference, AssetReferenceType.AnimationSegment, "AnimationTrack.AnimSegments.AnimReference");
                }
                break;
        }
    }

    private static void CollectObjectReferences(AssetReferenceGraph graph, string sourcePath, UObject export)
    {
        for (var i = 0; i < export.Properties.Count; i++)
        {
            var property = export.Properties[i];
            CollectProperty(graph, sourcePath, property.Tag, property.Name.Text, 0);
        }

        if (export is USkinnedMeshComponent component)
        {
            AddPackageIndexEdge(graph, sourcePath, component.GetSkeletalMesh(), AssetReferenceType.SkeletalMeshComponentMesh, "USkinnedMeshComponent.GetSkeletalMesh()");
        }
    }

    private static void CollectProperty(AssetReferenceGraph graph, string sourcePath, FPropertyTagType? property, string propertyPath, int depth)
    {
        if (property == null || depth > MaxPropertyDepth) return;

        switch (property)
        {
            case FPropertyTagType<FPackageIndex> objectProperty:
                AddPackageIndexEdge(graph, sourcePath, objectProperty.Value, InferReferenceType(propertyPath, false), propertyPath);
                break;
            case FPropertyTagType<FSoftObjectPath> softObjectProperty:
                AddSoftObjectPathEdge(graph, sourcePath, softObjectProperty.Value, InferReferenceType(propertyPath, true), propertyPath);
                break;
            case InterfaceProperty { Value.Object: { } interfaceObject }:
                AddPackageIndexEdge(graph, sourcePath, interfaceObject, InferReferenceType(propertyPath, false), propertyPath);
                break;
            case ArrayProperty { Value.Properties: { } array }:
                for (var i = 0; i < array.Count; i++)
                {
                    CollectProperty(graph, sourcePath, array[i], $"{propertyPath}[{i}]", depth + 1);
                }
                break;
            case SetProperty { Value.Properties: { } set }:
                for (var i = 0; i < set.Count; i++)
                {
                    CollectProperty(graph, sourcePath, set[i], $"{propertyPath}{{{i}}}", depth + 1);
                }
                break;
            case MapProperty { Value.Properties: { } map }:
                var index = 0;
                foreach (var (key, value) in map)
                {
                    CollectProperty(graph, sourcePath, key, $"{propertyPath}.key[{index}]", depth + 1);
                    CollectProperty(graph, sourcePath, value, $"{propertyPath}.value[{index}]", depth + 1);
                    index++;
                }
                break;
            case StructProperty { Value.StructType: FStructFallback fallback }:
                foreach (var tag in fallback.Properties)
                {
                    CollectProperty(graph, sourcePath, tag.Tag, $"{propertyPath}.{tag.Name.Text}", depth + 1);
                }
                break;
            case StructProperty { Value.StructType: FSoftObjectPath softObjectPath }:
                AddSoftObjectPathEdge(graph, sourcePath, softObjectPath, InferReferenceType(propertyPath, true), propertyPath);
                break;
            case OptionalProperty { Value: { } optional }:
                CollectProperty(graph, sourcePath, optional, $"{propertyPath}?", depth + 1);
                break;
        }
    }

    private static AssetReferenceType InferReferenceType(string propertyPath, bool isSoft)
    {
        if (propertyPath.Contains("Skeleton", StringComparison.OrdinalIgnoreCase)) return AssetReferenceType.Skeleton;
        if (propertyPath.Contains("PreviewSkeletalMesh", StringComparison.OrdinalIgnoreCase)) return AssetReferenceType.PreviewSkeletalMesh;
        if (propertyPath.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase) ||
            propertyPath.Contains("SkinnedAsset", StringComparison.OrdinalIgnoreCase)) return AssetReferenceType.SkeletalMeshComponentMesh;
        if (propertyPath.Contains("AnimClass", StringComparison.OrdinalIgnoreCase)) return AssetReferenceType.AnimationClass;
        if (propertyPath.Contains("AnimBlueprint", StringComparison.OrdinalIgnoreCase)) return AssetReferenceType.AnimationBlueprintReference;
        if (propertyPath.Contains("AnimReference", StringComparison.OrdinalIgnoreCase)) return AssetReferenceType.AnimationSegment;
        if (propertyPath.Contains("RetargetSourceAsset", StringComparison.OrdinalIgnoreCase)) return AssetReferenceType.RetargetSourceAsset;
        if (propertyPath.Contains("Parent", StringComparison.OrdinalIgnoreCase) ||
            propertyPath.Contains("Base", StringComparison.OrdinalIgnoreCase)) return AssetReferenceType.VariantParent;
        return isSoft ? AssetReferenceType.SoftReference : AssetReferenceType.HardReference;
    }

    private static void AddPackageIndexEdge(AssetReferenceGraph graph, string sourcePath, FPackageIndex? target, AssetReferenceType type, string propertyPath)
    {
        var targetPath = TryResolvePath(target);
        if (string.IsNullOrEmpty(targetPath)) return;
        graph.AddEdge(new AssetEdge
        {
            SourceAsset = sourcePath,
            TargetAsset = targetPath,
            ReferenceType = type,
            EvidenceSource = "FPackageIndex",
            PropertyPath = propertyPath,
            IsSoftReference = false
        });
    }

    private static void AddSoftObjectPathEdge(AssetReferenceGraph graph, string sourcePath, FSoftObjectPath target, AssetReferenceType type, string propertyPath)
    {
        var targetPath = NormalizeObjectPath(target.ToString());
        if (string.IsNullOrEmpty(targetPath) || targetPath.Equals("None", StringComparison.OrdinalIgnoreCase)) return;
        graph.AddEdge(new AssetEdge
        {
            SourceAsset = sourcePath,
            TargetAsset = targetPath,
            ReferenceType = type,
            EvidenceSource = "FSoftObjectPath",
            PropertyPath = propertyPath,
            IsSoftReference = true
        });
    }

    private static string? TryResolvePath(FPackageIndex? packageIndex)
    {
        if (packageIndex == null || packageIndex.IsNull) return null;
        return NormalizeObjectPath(packageIndex.ResolvedObject?.GetPathName());
    }

    private static string NormalizeObjectPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        if (path.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        return path.Replace('\\', '/');
    }

    private static WeightedBoneProfile TryBuildWeightedBoneProfile(USkinnedAsset mesh)
    {
        try
        {
            if (mesh.ReferenceSkeleton?.FinalRefBoneInfo == null || mesh.ReferenceSkeleton.FinalRefBoneInfo.Length == 0)
            {
                return new WeightedBoneProfile { ProfileStatus = "unavailable", UnavailableReason = "reference_skeleton_unavailable" };
            }

            if (mesh.LODModels == null || mesh.LODModels.Length == 0)
            {
                return new WeightedBoneProfile
                {
                    ProfileStatus = "unavailable",
                    UnavailableReason = "lod_models_unavailable",
                    SkeletonBones = mesh.ReferenceSkeleton.FinalRefBoneInfo.Length
                };
            }

            var bones = mesh.ReferenceSkeleton.FinalRefBoneInfo;
            var effectiveBones = new Dictionary<string, WeightedBoneInfo>(StringComparer.OrdinalIgnoreCase);
            var anyWeights = false;

            foreach (var lod in mesh.LODModels)
            {
                if (lod.Sections.Length == 0) continue;
                var weightedVertices = EnumerateWeightedVertices(lod).ToArray();
                foreach (var (vertexIndex, weights) in weightedVertices)
                {
                    var section = FindSectionForVertex(lod.Sections, vertexIndex);
                    if (section == null || section.BoneMap.Length == 0) continue;

                    for (var influence = 0; influence < weights.BoneIndex.Length && influence < weights.BoneWeight.Length; influence++)
                    {
                        var rawWeight = weights.BoneWeight[influence];
                        if (rawWeight == 0) continue;

                        var localBoneIndex = weights.BoneIndex[influence];
                        if (localBoneIndex >= section.BoneMap.Length) continue;

                        var skeletonBoneIndex = section.BoneMap[localBoneIndex];
                        if (skeletonBoneIndex < 0 || skeletonBoneIndex >= bones.Length) continue;

                        var boneName = bones[skeletonBoneIndex].Name.Text;
                        if (!effectiveBones.TryGetValue(boneName, out var info))
                        {
                            info = new WeightedBoneInfo();
                            effectiveBones[boneName] = info;
                        }

                        info.VertexCount++;
                        info.WeightSum += weights.bUse16BitBoneWeight ? rawWeight / 65535.0 : rawWeight / 255.0;
                        anyWeights = true;
                    }
                }
            }

            return new WeightedBoneProfile
            {
                ProfileStatus = anyWeights ? "available" : "unavailable",
                UnavailableReason = anyWeights ? null : "skin_weight_data_unavailable_or_empty",
                SkeletonBones = bones.Length,
                EffectiveBones = effectiveBones
            };
        }
        catch (Exception ex)
        {
            return new WeightedBoneProfile
            {
                ProfileStatus = "unavailable",
                UnavailableReason = ex.GetType().Name
            };
        }
    }

    private static IEnumerable<(int VertexIndex, FSkinWeightInfo Weights)> EnumerateWeightedVertices(FStaticLODModel lod)
    {
        if (lod.VertexBufferGPUSkin.VertsFloat.Length > 0)
        {
            for (var i = 0; i < lod.VertexBufferGPUSkin.VertsFloat.Length; i++)
                if (lod.VertexBufferGPUSkin.VertsFloat[i].Infs is { } weights)
                    yield return (i, weights);
        }
        else if (lod.VertexBufferGPUSkin.VertsHalf.Length > 0)
        {
            for (var i = 0; i < lod.VertexBufferGPUSkin.VertsHalf.Length; i++)
                if (lod.VertexBufferGPUSkin.VertsHalf[i].Infs is { } weights)
                    yield return (i, weights);
        }
        else if (lod.VertexBufferGPUSkin.VertsFloatPacked.Length > 0)
        {
            for (var i = 0; i < lod.VertexBufferGPUSkin.VertsFloatPacked.Length; i++)
                if (lod.VertexBufferGPUSkin.VertsFloatPacked[i].Infs is { } weights)
                    yield return (i, weights);
        }
        else if (lod.VertexBufferGPUSkin.VertsHalfPacked.Length > 0)
        {
            for (var i = 0; i < lod.VertexBufferGPUSkin.VertsHalfPacked.Length; i++)
                if (lod.VertexBufferGPUSkin.VertsHalfPacked[i].Infs is { } weights)
                    yield return (i, weights);
        }

        foreach (var section in lod.Sections)
        {
            for (var i = 0; i < section.SoftVertices.Length; i++)
                if (section.SoftVertices[i].Infs is { } weights)
                    yield return ((int)section.BaseVertexIndex + i, weights);
        }
    }

    private static FSkelMeshSection? FindSectionForVertex(IEnumerable<FSkelMeshSection> sections, int vertexIndex)
    {
        foreach (var section in sections)
        {
            var start = section.BaseVertexIndex;
            var count = section.NumVertices > 0 ? section.NumVertices : section.SoftVertices.Length;
            if (vertexIndex >= start && vertexIndex < start + count) return section;
        }
        return null;
    }
}
