using System.Numerics;
using Eclipse;

internal static class CapeTests
{
    internal static Task HangingWithoutClipping()
    {
        foreach (var pose in Enum.GetValues<StandingPose>())
        foreach (var width in new[] { 3f, 4f })
        foreach (var phase in new[] { 0f, 2f, 4f })
        {
            var rig = new SkinPoseRig(pose, phase);
            rig.SetArmWidth(width);
            var parts = new List<(SkinBone Bone, Vector3 Min, Vector3 Max)>();
            void Part(SkinBone bone, float x, float bottom, float w, float h, float d, float grow = .24f) =>
                parts.Add((bone, new(x - w / 2 - grow, bottom - grow, -d / 2 - grow),
                    new(x + w / 2 + grow, bottom + h + grow, d / 2 + grow)));
            Part(SkinBone.Torso, 0, 12, 8, 12, 4);
            Part(SkinBone.Head, 0, 24, 8, 8, 8, .5f);
            Part(SkinBone.RightUpperArm, -(4 + width / 2), 18, width, 6, 4);
            Part(SkinBone.RightForearm, -(4 + width / 2), 12, width, 6, 4);
            Part(SkinBone.LeftUpperArm, 4 + width / 2, 18, width, 6, 4);
            Part(SkinBone.LeftForearm, 4 + width / 2, 12, width, 6, 4);
            Part(SkinBone.RightThigh, -2, 6, 4, 6, 4);
            Part(SkinBone.RightShin, -2, 0, 4, 6, 4);
            Part(SkinBone.LeftThigh, 2, 6, 4, 6, 4);
            Part(SkinBone.LeftShin, 2, 0, 4, 6, 4);
            var anchor = SkinPoseRig.CapeAnchor;
            var previous = Vector3.Transform(anchor, rig.Transforms[(int)SkinBone.Cape]);
            for (var frame = 0; frame < 200; frame++)
            {
                var cape = rig.Transforms[(int)SkinBone.Cape];
                var top = Vector3.Transform(anchor, cape);
                var bottom = Vector3.Transform(anchor - Vector3.UnitY * SkinPoseRig.CapeHeight, cape);
                Require(Vector3.Normalize(top - bottom).Y > .998f, "Cape must hang down under gravity");
                Require(Vector3.Distance(top, Vector3.Transform(anchor, rig.Transforms[(int)SkinBone.Torso])) < .0001f,
                    "Cape clasp must follow the upper back");
                Require(Vector3.Distance(top, previous) < .05f, "Cape must move smoothly during idle");
                foreach (var part in parts)
                {
                    Matrix4x4.Invert(rig.Transforms[(int)part.Bone], out var inverse);
                    var relative = cape * inverse;
                    // Check both cloth surfaces against the skin including its outer clothing layer.
                    for (var x = -SkinPoseRig.CapeWidth / 2f; x <= SkinPoseRig.CapeWidth / 2f; x += .5f)
                    for (var y = anchor.Y - SkinPoseRig.CapeHeight; y <= anchor.Y; y += .5f)
                    for (var side = -1; side <= 1; side += 2)
                    {
                        var p = Vector3.Transform(new(x, y, anchor.Z + side * SkinPoseRig.CapeThickness / 2f), relative);
                        if (p.X > part.Min.X && p.X < part.Max.X && p.Y > part.Min.Y && p.Y < part.Max.Y && p.Z > part.Min.Z && p.Z < part.Max.Z)
                            throw new InvalidOperationException($"Cape clips {part.Bone}: {pose}, arms {width}, phase {phase}, t={rig.Seconds:F2}, point={p}");
                    }
                }
                previous = top;
                rig.Advance(.05);
            }
        }
        return Task.CompletedTask;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
