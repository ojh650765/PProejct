using System.Collections.Generic;
using NUnit.Framework;
using PokeLab.Overworld;
using UnityEngine;

public sealed class WalkableGroundTests
{
    private readonly List<GameObject> objects = new List<GameObject>();
    // Far outside scene content so the test cannot accidentally sample the open level.
    private static readonly Vector3 Origin = new Vector3(5000, 100, 5000);

    private GameObject Surface(string layer, float top, Vector3 size)
    {
        var go = new GameObject("WalkableTest");
        objects.Add(go);
        go.layer = LayerMask.NameToLayer(layer);
        Assert.That(go.layer, Is.GreaterThanOrEqualTo(0), layer + " layer must exist");
        var box = go.AddComponent<BoxCollider>();
        box.size = size;
        go.transform.position = Origin + Vector3.up * (top-size.y*.5f);
        Physics.SyncTransforms();
        return go;
    }

    [TearDown] public void TearDown()
    {
        foreach (var go in objects) Object.DestroyImmediate(go);
        objects.Clear();
    }

    [Test] public void PropTopCannotReplaceTheGroundBelowIt()
    {
        Surface("Ground", 0, new Vector3(8,.2f,8));
        Surface("Environment", 1.4f, new Vector3(1,1.4f,1));
        Assert.That(WalkableGround.TrySample(Origin+Vector3.up*1.4f, out var floor), Is.True);
        Assert.That(floor.y, Is.EqualTo(Origin.y).Within(.01f));
    }

    [Test] public void SubmergedGroundIsRejected()
    {
        Surface("Ground", -1, new Vector3(8,.2f,8));
        Surface("Water", 0, new Vector3(8,.1f,8));
        Assert.That(WalkableGround.TrySample(Origin, out _), Is.False);
    }

    [Test] public void AnExplicitBridgeDeckAboveWaterIsWalkable()
    {
        Surface("Ground", -1, new Vector3(8,.2f,8));
        Surface("Water", 0, new Vector3(8,.1f,8));
        Surface("Ground", .5f, new Vector3(2,.2f,8));
        Assert.That(WalkableGround.TrySample(Origin, out var floor), Is.True);
        Assert.That(floor.y, Is.EqualTo(Origin.y+.5f).Within(.01f));
    }

    [Test] public void AShortScriptedStepCannotClimbACliff()
    {
        Surface("Ground", 1, new Vector3(8,.2f,8));
        Assert.That(WalkableGround.TryStep(Origin, Origin+Vector3.right*.1f, out _), Is.False);
    }
}
