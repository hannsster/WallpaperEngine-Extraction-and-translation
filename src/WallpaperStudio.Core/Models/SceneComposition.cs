namespace WallpaperStudio.Models;

/// <summary>
/// How much of a scene is baked into the single static frame that gets exported.
/// A scene has no video timeline, so "choosing a frame" means choosing which
/// layers are composed together.
/// </summary>
public enum SceneComposition
{
    /// <summary>Background plus every normal-scale object, including small props.</summary>
    Balanced,

    /// <summary>Every drawable layer, including stretched gradients and effect layers.</summary>
    Full,

    /// <summary>Only layers that cover (nearly) the whole canvas.</summary>
    Background
}

/// <summary>Display metadata for the review-screen composition picker.</summary>
public sealed record SceneCompositionOption(SceneComposition Value, string Label, string Description)
{
    public override string ToString() => Label;
}
