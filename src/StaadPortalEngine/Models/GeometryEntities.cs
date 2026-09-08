using System;
using System.Collections.Generic;

namespace StaadPortalEngine.Models
{
    public enum MemberType
    {
        Column = 0,
        IntermediateColumn = 1,
        Rafter = 2,
        EaveStrut = 3,
        RidgeStrut = 4,
        RccWallTieBeam = 5,
        GablePost = 6,
        RoofBracing = 7,
        WallBracing = 8,
        CableTrayBracket = 9,
        CableTrayRunner = 10,
        CraneCorbel = 9, // Backward compatibility alias
        MezzanineMainBeam = 11,
        MezzanineSecondaryBeam = 12,
        MezzanineColumn = 13,
        PortalBeam = 14,
        PortalKneeBrace = 15,
        JackBeam = 16,
        CanopyRafter = 17,
        CanopyRunner = 18,
        CanopyBracing = 19
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
        public string SectionProperty { get; set; } = string.Empty;

        public Beam3D() { }

        public Beam3D(int id, int nodeA, int nodeB, MemberType type, string groupName = "", string sectionProperty = "")
        {
            Id = id;
            NodeA = nodeA;
            NodeB = nodeB;
            Type = type;
            GroupName = string.IsNullOrEmpty(groupName) ? type.ToString().ToUpperInvariant() : groupName;
            SectionProperty = sectionProperty ?? string.Empty;
        }

        public override string ToString() => $"Beam {Id} [{Type}]: ({NodeA} -> {NodeB})";
    }

    public class Template2DNode
    {
        public int Id { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public bool IsBase { get; set; }
        public string SupportType { get; set; } = string.Empty;
    }

    public class Template2DBeam
    {
        public int Id { get; set; }
        public int NodeA { get; set; }
        public int NodeB { get; set; }
        public MemberType Type { get; set; }
        public string GroupName { get; set; } = string.Empty;
        public string SectionProperty { get; set; } = string.Empty;
        public bool StartReleaseMyMz { get; set; }
        public bool EndReleaseMyMz { get; set; }
        public bool Beta90 { get; set; }
    }

    public class Template2DFrameDefinition
    {
        public List<Template2DNode> Nodes { get; set; } = new();
        public List<Template2DBeam> Beams { get; set; } = new();
        public List<string> LoadLines { get; set; } = new();
        public string PreservedPreamble { get; set; } = string.Empty;
        public string PreservedPostamble { get; set; } = string.Empty;
        public List<string> PreservedCommandBlocks { get; set; } = new();
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
        public Dictionary<int, List<int>> TemplateBeamTo3DBeamMap { get; set; } = new();
        public Template2DFrameDefinition? TemplateFrame { get; set; }
        public string CustomPropertyText { get; set; } = string.Empty;
    }
}
