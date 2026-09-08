using System;
using System.Collections.Generic;
using System.Linq;
using StaadPortalEngine.Helpers;

namespace StaadPortalEngine.Models
{
    public class SpanDefinition
    {
        public double SpanWidth { get; set; } = 24.0;
        public double EaveHeightLeft { get; set; } = 7.0;
        public double EaveHeightRight { get; set; } = 7.0;
        public double RoofSlopeRatio { get; set; } = 10.0; // 1 in 10

        public double RidgeHeight
        {
            get
            {
                double halfSpan = SpanWidth / 2.0;
                double rise = halfSpan / (RoofSlopeRatio > 0 ? RoofSlopeRatio : 10.0);
                return Math.Max(EaveHeightLeft, EaveHeightRight) + rise;
            }
        }

        public SpanDefinition() { }

        public SpanDefinition(double width, double eaveHeight, double slopeRatio = 10.0)
        {
            SpanWidth = width;
            EaveHeightLeft = eaveHeight;
            EaveHeightRight = eaveHeight;
            RoofSlopeRatio = slopeRatio;
        }
    }

    public class CableTrayConfiguration
    {
        public bool Enabled { get; set; } = false;
        public string Location { get; set; } = "side_intermediate"; // "side", "side_intermediate", "all"
        public double BracketHeight { get; set; } = 5.0; // Cable tray height [m]
        public double BracketLength { get; set; } = 0.8; // Cable tray projection [m]
        public bool OnLeftExteriorColumn { get; set; } = true;
        public bool OnRightExteriorColumn { get; set; } = true;
        public bool OnInteriorColumns { get; set; } = true;
        public bool OnGablePosts { get; set; } = false;
    }

    // Alias for backward compatibility
    public class CraneConfiguration : CableTrayConfiguration { }

    public class GableWallConfiguration
    {
        public string GableBaySpacingFront { get; set; } = "1@5+2@4+1@5";
        public string GableBaySpacingRear { get; set; } = "4@6";
        public int WindPostsStartEnd { get; set; } = 3;
        public int WindPostsEndWall { get; set; } = 3;
    }

    public class BracingConfiguration
    {
        public bool IncludeRoofBracing { get; set; } = true;
        public bool IncludeWallBracing { get; set; } = true;
        public double MaxBraceLength { get; set; } = 12.5; // Maximum allowable roof cross brace diagonal length [m]
        public BracingType Type { get; set; } = BracingType.XBrace;
        public List<int> BracedBayIndices { get; set; } = new() { 0, 4, 9 };

        // Portal Frame Bracing (Open Bay Portal Bracing for vehicle / equipment / door access)
        public bool EnablePortalBracing { get; set; } = false;
        public double PortalBracingHeight { get; set; } = 4.5;
        public double PortalLegOffset { get; set; } = 0.5; // Offset of inner leg at portal beam level [m] (e.g. 0.500m)
        public List<int> PortalBracedBayIndices { get; set; } = new();

        // Left & Right Wall Portal Position & Bay controls
        public bool PortalOnLeftWall { get; set; } = true;
        public bool PortalOnRightWall { get; set; } = true;
        public List<int> PortalLeftBayIndices { get; set; } = new();
        public List<int> PortalRightBayIndices { get; set; } = new();

        public bool IsPortalBayLeft(int b)
        {
            if (!EnablePortalBracing || !PortalOnLeftWall) return false;
            var bays = (PortalLeftBayIndices != null && PortalLeftBayIndices.Count > 0)
                ? PortalLeftBayIndices
                : PortalBracedBayIndices;
            return bays != null && bays.Contains(b);
        }

        public bool IsPortalBayRight(int b)
        {
            if (!EnablePortalBracing || !PortalOnRightWall) return false;
            var bays = (PortalRightBayIndices != null && PortalRightBayIndices.Count > 0)
                ? PortalRightBayIndices
                : PortalBracedBayIndices;
            return bays != null && bays.Contains(b);
        }
    }

    public class MezzanineConfiguration
    {
        public bool Enabled { get; set; } = false;
        public double FloorHeight { get; set; } = 3.5;
        public double StartX { get; set; } = 0.0;
        public double EndX { get; set; } = 12.0;
        public List<int> BayIndices { get; set; } = new() { 0, 1 };
        public double JoistSpacing { get; set; } = 1.5;
    }

    public class JackPortalConfiguration
    {
        public bool Enabled { get; set; } = false;
        public string IntermediateBaySpacingExpression { get; set; } = "2@12+1@6";
        public List<double> IntermediateBaySpacings { get; set; } = new();
    }

    public class CanopySideConfiguration
    {
        public bool Enabled { get; set; } = false;
        public double Height { get; set; } = 5.0; // Canopy attachment height [m]
        public double Projection { get; set; } = 4.0; // Outward projection length [m]
        public double Drop { get; set; } = 0.2; // Slope drop from column to tip [m]
        public string BayExpression { get; set; } = ""; // e.g. "1, 2, 3" (empty = all bays)
        public List<int> BayIndices { get; set; } = new();

        public bool IsInBay(int bayIndex, int totalBays)
        {
            if (!Enabled) return false;
            if (BayIndices != null && BayIndices.Count > 0)
            {
                return BayIndices.Contains(bayIndex);
            }
            if (!string.IsNullOrWhiteSpace(BayExpression))
            {
                var parsed = SpacingParser.ParseBayIndices(BayExpression);
                if (parsed.Count > 0) return parsed.Contains(bayIndex);
            }
            // If empty, applies to all bays
            return true;
        }
    }

    public class CanopyConfiguration
    {
        public bool Enabled => (LeftWall?.Enabled ?? false) || (RightWall?.Enabled ?? false) ||
                               (FrontWall?.Enabled ?? false) || (RearWall?.Enabled ?? false);
        public CanopySideConfiguration LeftWall { get; set; } = new();
        public CanopySideConfiguration RightWall { get; set; } = new();
        public CanopySideConfiguration FrontWall { get; set; } = new();
        public CanopySideConfiguration RearWall { get; set; } = new();
    }

    public class PortalConfiguration
    {
        public string ModelName { get; set; } = "3D Industrial Portal Frame Shed";
        public FrameType FrameType { get; set; } = FrameType.SingleGable;
        public string SpanMode { get; set; } = "1"; // "1", "2", "custom"
        public List<SpanDefinition> Spans { get; set; } = new();

        public string WidthModuleExpression { get; set; } = "2@12";

        public string BaySpacingExpression { get; set; } = "5@6+5@4";
        public List<double> BaySpacings { get; set; } = new() { 6.0, 6.0, 6.0, 6.0, 6.0, 4.0, 4.0, 4.0, 4.0, 4.0 };

        public double RccWallHeight { get; set; } = 2.2;
        public GableWallConfiguration GableWalls { get; set; } = new();
        public CableTrayConfiguration CableTray { get; set; } = new();
        public CableTrayConfiguration Crane { get => CableTray; set => CableTray = value; } // Backward compatibility
        public BracingConfiguration Bracing { get; set; } = new();
        public MezzanineConfiguration Mezzanine { get; set; } = new();
        public JackPortalConfiguration JackPortal { get; set; } = new();
        public CanopyConfiguration Canopy { get; set; } = new();

        // 2D to 3D Conversion Properties
        public string GenerationMode { get; set; } = "parametric"; // "parametric" or "convert2d"
        public string? Template2DStdText { get; set; }
        public Template2DFrameDefinition? Template2DFrame { get; set; }

        public double TotalWidth => Spans?.Sum(s => s.SpanWidth) ?? 0.0;
        public double TotalLength => BaySpacings?.Sum() ?? 0.0;
        public int TotalBays => BaySpacings?.Count ?? 0;
        public int TotalFrames => TotalBays + 1;
    }
}
