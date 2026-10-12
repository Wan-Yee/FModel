using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Exporters;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Relationships;
using FModel.Framework;
using FModel.Views.Resources.Controls;
using Serilog;
using CUE4ParseObject = CUE4Parse.UE4.Assets.Exports.UObject;

namespace FModel.Services.AssetRelationships;

public sealed class RelationshipExportCoordinator
{
    public static RelationshipExportCoordinator Instance { get; } = new();

    private static readonly ILogger Log = Serilog.Log.ForContext<RelationshipExportCoordinator>();
    private readonly object _gate = new();
    private AssetRelationshipIndex? _index;
    private IFileProvider? _provider;
    private readonly Dictionary<string, IReadOnlyList<MeshAnimationRelationship>> _relationshipsByMesh = new(StringComparer.OrdinalIgnoreCase);
    private bool _warnedIncompleteGraph;

    private RelationshipExportCoordinator()
    {
    }

    public void Reset()
    {
        lock (_gate)
        {
            _index = null;
            _provider = null;
            _relationshipsByMesh.Clear();
            _warnedIncompleteGraph = false;
        }
    }

    public void QueueRelatedAnimations(
        USkinnedAsset mesh,
        IFileProvider provider,
        ExportSession session,
        ExportOptions options)
    {
        if (!ShouldUseRelationshipExport(options)) return;

        AssetRelationshipIndex index;
        lock (_gate)
        {
            if (!ReferenceEquals(_provider, provider))
            {
                _provider = provider;
                _index = null;
                _relationshipsByMesh.Clear();
                _warnedIncompleteGraph = false;
            }

            _index ??= new AssetRelationshipIndexer(provider).Build(CancellationToken.None, options.ExportMeshProfiles);
            index = _index;
        }

        var meshPath = NormalizeObjectPath(mesh.GetPathName());
        if (!index.SkeletalMeshByPath.TryGetValue(meshPath, out var meshProfile))
        {
            Log.Warning("Relationship index did not contain mesh {MeshPath}; manifest will use skeleton fallback only", meshPath);
            return;
        }

        var resolver = new MeshAnimationRelationshipResolver();
        var relationships = resolver.Resolve(meshProfile, index.Animations, index.ReferenceGraph);
        var selected = SelectForExport(relationships, options).ToArray();

        lock (_gate)
        {
            _relationshipsByMesh[meshPath] = relationships;
        }

        if (!relationships.Any(x => x.RelationshipLevel is MeshAnimationRelationshipLevel.DirectReference or MeshAnimationRelationshipLevel.StrongCandidate) &&
            !_warnedIncompleteGraph)
        {
            _warnedIncompleteGraph = true;
            FLogger.Append(ELog.Warning, () => FLogger.Text("Animation relationship graph has no direct/strong evidence for at least one exported mesh; Skeleton-only candidates are preserved in manifests. Use All Candidates or Legacy if needed.", Constants.WHITE, true));
        }

        foreach (var relationship in selected)
        {
            if (!index.AnimationByPath.TryGetValue(relationship.AnimationAssetPath, out var animationMetadata)) continue;
            if (!provider.TryLoadPackageObject(animationMetadata.AssetPath, out CUE4ParseObject export) || export is not UAnimationAsset animation) continue;

            session.Add(new AnimationExporter(animation)
            {
                OutputFolderOverride = GetAnimationOutputFolder(animationMetadata, options),
                OutputFileNameOverride = GetAnimationOutputFileName(animationMetadata, options)
            });
        }
    }

    public void WriteManifests(string exportDirectory, ExportOptions options, IReadOnlyList<ExportResult>? results)
    {
        if (results == null || !options.ExportRelationshipManifest && !options.ExportMeshProfiles) return;

        AssetRelationshipIndex? index;
        IReadOnlyDictionary<string, IReadOnlyList<MeshAnimationRelationship>> relationshipsByMesh;
        lock (_gate)
        {
            index = _index;
            relationshipsByMesh = new Dictionary<string, IReadOnlyList<MeshAnimationRelationship>>(_relationshipsByMesh, StringComparer.OrdinalIgnoreCase);
        }

        if (index == null || relationshipsByMesh.Count == 0) return;

        try
        {
            RelationshipManifestWriter.Write(exportDirectory, options, index, relationshipsByMesh, results);
            FLogger.Append(ELog.Information, () => FLogger.Text("Wrote animation relationship manifests and indexes.", Constants.WHITE, true));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to write relationship manifests");
            FLogger.Append(ex);
        }
    }

    private static bool ShouldUseRelationshipExport(ExportOptions options)
    {
        return options.ExportAnimations &&
               options.AnimationRelationshipExportMode != EAnimationRelationshipExportMode.LegacySkeletonExport;
    }

    private static IEnumerable<MeshAnimationRelationship> SelectForExport(
        IEnumerable<MeshAnimationRelationship> relationships,
        ExportOptions options)
    {
        foreach (var relationship in relationships)
        {
            if (relationship.CompatibilityStatus != MeshAnimationCompatibilityStatus.Compatible) continue;

            switch (options.AnimationRelationshipExportMode)
            {
                case EAnimationRelationshipExportMode.ReferencedAnimationsOnly
                    when relationship.RelationshipLevel == MeshAnimationRelationshipLevel.DirectReference:
                    yield return relationship;
                    break;
                case EAnimationRelationshipExportMode.ReferencedAndStrongCandidates
                    when relationship.RelationshipLevel is MeshAnimationRelationshipLevel.DirectReference or MeshAnimationRelationshipLevel.StrongCandidate:
                    yield return relationship;
                    break;
                case EAnimationRelationshipExportMode.AllCandidatesAndManifest
                    when relationship.RelationshipLevel is MeshAnimationRelationshipLevel.DirectReference or MeshAnimationRelationshipLevel.StrongCandidate or MeshAnimationRelationshipLevel.WeakCandidate or MeshAnimationRelationshipLevel.SkeletonOnly:
                    if (relationship.RelationshipLevel != MeshAnimationRelationshipLevel.WeakCandidate || options.IncludeWeakCandidates)
                        yield return relationship;
                    break;
            }
        }
    }

    private static string? GetAnimationOutputFolder(AnimationMetadata animation, ExportOptions options)
    {
        if (!options.UseSharedAnimationLibrary) return null;

        var skeletonName = string.IsNullOrWhiteSpace(animation.SkeletonPath)
            ? "Unknown_Skeleton"
            : AssetReferenceGraph.GetAssetName(animation.SkeletonPath);
        return $"AnimationLibrary/{SanitizeFolderName(skeletonName)}";
    }

    private static string? GetAnimationOutputFileName(AnimationMetadata animation, ExportOptions options)
    {
        if (!options.UseSharedAnimationLibrary) return null;

        return $"{SanitizeFolderName(animation.AssetName)}.{ShortHash(animation.AssetPath)}";
    }

    private static string SanitizeFolderName(string name)
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

    private static string NormalizeObjectPath(string path) => path.Replace('\\', '/');
}
