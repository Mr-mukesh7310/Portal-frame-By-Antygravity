using System;
using System.Collections.Generic;

namespace StaadPortalEngine.Models
{
    public enum MemberType
    {
        Column,
        IntermediateColumn,
        Rafter,
        EaveStrut,
        RidgeStrut,
        RccWallTieBeam,
        GablePost,
        RoofBracing,
        WallBracing,
        CableTrayBracket,
        CableTrayRunner,
        CraneCorbel = CableTrayBracket, // Backward compatibility alias
        MezzanineMainBeam,
        MezzanineSecondaryBeam,
        MezzanineColumn,
        PortalBeam,
        PortalKneeBrace,
        JackBeam
    }

    public enum FrameType
    {
        SingleGable,
        MultiSpanGable,
        Monoslope
    }

    public enum BracingType
    {
        XBrace,
        InvertedV
    }

    public class Node3D
    {
        public int Id { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }

        public Node3D() { }

        public Node3D(int id, double x, double y, double z)
        {
            Id = id;
            X = Math.Round(x, 4);
            Y = Math.Round(y, 4);
            Z = Math.Round(z, 4);
        }

        public override string ToString() => $"Node {Id}: ({X:F3}, {Y:F3}, {Z:F3})";
    }

    public class Beam3D
    {
        public int Id { get; set; }
        public int NodeA { get; set; }
        public int NodeB { get; set; }
        public MemberType Type { get; set; }
        public string GroupName { get; set; } = string.Empty;

        public Beam3D() { }

        public Beam3D(int id, int nodeA, int nodeB, MemberType type, string groupName = "")
        {
            Id = id;
            NodeA = nodeA;
            NodeB = nodeB;
            Type = type;
            GroupName = string.IsNullOrEmpty(groupName) ? type.ToString().ToUpperInvariant() : groupName;
        }

        public override string ToString() => $"Beam {Id} [{Type}]: ({NodeA} -> {NodeB})";
    }

    public class GeneratedModel
    {
        public PortalConfiguration Configuration { get; set; } = new();
        public Dictionary<int, Node3D> Nodes { get; set; } = new();
        public Dictionary<int, Beam3D> Beams { get; set; } = new();
        public List<int> BaseNodeIds { get; set; } = new();
        public List<int> FixedBaseNodeIds { get; set; } = new();
        public List<int> PinnedBaseNodeIds { get; set; } = new();
        public List<int> TensionOnlyBeamIds { get; set; } = new();
        public List<int> PinnedEndBeamIds { get; set; } = new();
        public List<int> StartReleaseMyMzBeamIds { get; set; } = new();
        public List<int> EndReleaseMyMzBeamIds { get; set; } = new();
        public List<int> Beta90BeamIds { get; set; } = new();
    }
}
