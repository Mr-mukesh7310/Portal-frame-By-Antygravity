using System;
using System.Collections.Generic;
using System.Linq;
using StaadPortalEngine.Helpers;
using StaadPortalEngine.Models;
using StaadPortalEngine.Parsers;

namespace StaadPortalEngine.Generators
{
    public class PortalGeometryBuilder
    {
        private int _nodeCounter = 1;
        private int _beamCounter = 1;

        private readonly Dictionary<int, Node3D> _nodes = new();
        private readonly Dictionary<int, Beam3D> _beams = new();
        private readonly List<int> _baseNodeIds = new();
        private readonly List<int> _fixedBaseNodeIds = new();
        private readonly List<int> _pinnedBaseNodeIds = new();
        private readonly List<int> _tensionBeamIds = new();
        private readonly List<int> _pinnedBeamIds = new();
        private readonly List<int> _startReleaseMyMzBeamIds = new();
        private readonly List<int> _endReleaseMyMzBeamIds = new();
        private readonly List<int> _beta90BeamIds = new();

        private Node3D AddNode(double x, double y, double z)
        {
            x = Math.Round(x, 4);
            y = Math.Round(y, 4);
            z = Math.Round(z, 4);

            var existing = _nodes.Values.FirstOrDefault(n =>
                Math.Abs(n.X - x) < 0.001 &&
                Math.Abs(n.Y - y) < 0.001 &&
                Math.Abs(n.Z - z) < 0.001);

            if (existing != null)
                return existing;

            var node = new Node3D(_nodeCounter++, x, y, z);
            _nodes.Add(node.Id, node);

            if (Math.Abs(y) < 0.0001 && !_baseNodeIds.Contains(node.Id))
            {
                _baseNodeIds.Add(node.Id);
            }

            return node;
        }

        private void AddFixedBaseNode(Node3D node)
        {
            if (!_fixedBaseNodeIds.Contains(node.Id)) _fixedBaseNodeIds.Add(node.Id);
            if (!_baseNodeIds.Contains(node.Id)) _baseNodeIds.Add(node.Id);
        }

        private void AddPinnedBaseNode(Node3D node)
        {
            if (!_pinnedBaseNodeIds.Contains(node.Id)) _pinnedBaseNodeIds.Add(node.Id);
            if (!_baseNodeIds.Contains(node.Id)) _baseNodeIds.Add(node.Id);
        }

        private Beam3D? AddBeam(int nodeA, int nodeB, MemberType type, string groupName = "", string sectionProperty = "")
        {
            if (nodeA == nodeB) return null;

            var existing = _beams.Values.FirstOrDefault(b =>
                (b.NodeA == nodeA && b.NodeB == nodeB) ||
                (b.NodeA == nodeB && b.NodeB == nodeA));

            if (existing != null)
                return existing;

            var beam = new Beam3D(_beamCounter++, nodeA, nodeB, type, groupName, sectionProperty);
            _beams.Add(beam.Id, beam);

            if (type == MemberType.RoofBracing || type == MemberType.WallBracing)
            {
                _tensionBeamIds.Add(beam.Id);
            }
            else if (type == MemberType.EaveStrut || type == MemberType.RidgeStrut || type == MemberType.RccWallTieBeam || type == MemberType.MezzanineSecondaryBeam)
            {
                _pinnedBeamIds.Add(beam.Id);
            }

            return beam;
        }

        public GeneratedModel Build(PortalConfiguration config)
        {
            if (config.GenerationMode == "convert2d" || (!string.IsNullOrWhiteSpace(config.Template2DStdText) && config.GenerationMode != "parametric"))
            {
                return BuildFrom2DTemplate(config);
            }

            _nodes.Clear();
            _beams.Clear();
            _baseNodeIds.Clear();
            _fixedBaseNodeIds.Clear();
            _pinnedBaseNodeIds.Clear();
            _tensionBeamIds.Clear();
            _pinnedBeamIds.Clear();
            _startReleaseMyMzBeamIds.Clear();
            _endReleaseMyMzBeamIds.Clear();
            _beta90BeamIds.Clear();
            _nodeCounter = 1;
            _beamCounter = 1;

            if (config.Spans == null || config.Spans.Count == 0)
            {
                config.Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 10.0) };
            }

            // Parse bay spacing expression along Z (e.g. "5@6+5@4" or "0" for single mid frame)
            bool isZeroBayExpression = !string.IsNullOrWhiteSpace(config.BaySpacingExpression) &&
                (config.BaySpacingExpression.Trim() == "0" ||
                 config.BaySpacingExpression.Trim() == "0.0" ||
                 (double.TryParse(config.BaySpacingExpression.Trim(), out double zeroVal) && zeroVal == 0.0 && !config.BaySpacingExpression.Contains('@')));

            if (isZeroBayExpression)
            {
                config.BaySpacings = new List<double>();
            }
            else if (!string.IsNullOrWhiteSpace(config.BaySpacingExpression))
            {
                config.BaySpacings = SpacingParser.ParseBaySpacings(config.BaySpacingExpression);
            }
            else if (config.BaySpacings == null || config.BaySpacings.Count == 0)
            {
                config.BaySpacings = new List<double> { 6.0, 6.0, 6.0, 6.0 };
            }

            bool isSingleMidFrame = isZeroBayExpression ||
                                    config.BaySpacings.Count == 0 ||
                                    (config.BaySpacings.Count == 1 && config.BaySpacings[0] == 0.0);

            if (isSingleMidFrame)
            {
                config.BaySpacings = new List<double>();
            }

            double totalWidth = config.TotalWidth;
            double totalLength = isSingleMidFrame ? 0.0 : config.TotalLength;

            // Frame 0 at Z = TotalLength, stepping down to Z = 0 (or single mid frame at Z = 0)
            List<double> zCoords = isSingleMidFrame ? new() { 0.0 } : new() { totalLength };
            if (!isSingleMidFrame)
            {
                double currentZ = totalLength;
                foreach (var spacing in config.BaySpacings)
                {
                    currentZ -= spacing;
                    zCoords.Add(Math.Round(currentZ, 4));
                }
            }

            // Parse Width Modules (Interior intermediate columns for dividing span)
            List<double> widthModuleOffsets = new();
            if (!string.IsNullOrWhiteSpace(config.WidthModuleExpression))
            {
                widthModuleOffsets = SpacingParser.ParseGablePostOffsets(config.WidthModuleExpression, totalWidth);
            }

            // Gable post offsets for Front and Rear
            string frontExpr = !string.IsNullOrWhiteSpace(config.GableWalls?.GableBaySpacingFront)
                ? config.GableWalls.GableBaySpacingFront
                : config.GableWalls?.WindPostsStartEnd.ToString() ?? "3";
            var frontPostOffsets = SpacingParser.ParseGablePostOffsets(frontExpr, totalWidth);

            string rearExpr = !string.IsNullOrWhiteSpace(config.GableWalls?.GableBaySpacingRear)
                ? config.GableWalls.GableBaySpacingRear
                : config.GableWalls?.WindPostsEndWall.ToString() ?? "3";
            var rearPostOffsets = SpacingParser.ParseGablePostOffsets(rearExpr, totalWidth);

            // Cable Tray Location options:
            // "side" -> Side wall only
            // "side_intermediate" -> Side wall + Intermediate
            // "all" -> Side wall + Intermediate + Gable
            string cableLoc = config.CableTray?.Location?.ToLowerInvariant() ?? "side_intermediate";
            bool cableTrayEnabled = config.CableTray?.Enabled ?? false;
            bool includeSide = cableTrayEnabled;
            bool includeIntermediate = cableTrayEnabled && (cableLoc == "side_intermediate" || cableLoc == "all");
            bool includeGable = cableTrayEnabled && (cableLoc == "all");

            var frameRecords = new List<FrameRecord>();

            // Jack Portal Configuration (Custom Intermediate Column Spacing along Z from Front to Rear and Jack Beams)
            bool isJackPortalEnabled = config.JackPortal != null && config.JackPortal.Enabled;
            var jackColZList = new List<double>();
            if (isJackPortalEnabled && !string.IsNullOrWhiteSpace(config.JackPortal?.IntermediateBaySpacingExpression))
            {
                var intSpacings = SpacingParser.ParseBaySpacings(config.JackPortal.IntermediateBaySpacingExpression);
                jackColZList.Add(totalLength);
                double currentJackZ = totalLength;
                foreach (var s in intSpacings)
                {
                    currentJackZ -= s;
                    if (currentJackZ > 0.05)
                    {
                        jackColZList.Add(Math.Round(currentJackZ, 4));
                    }
                }
                if (!jackColZList.Any(zVal => Math.Abs(zVal) < 0.05))
                {
                    jackColZList.Add(0.0);
                }
            }

            // Helper to check whether a longitudinal bay between zA and zB along X = mx contains a Jack Beam
            bool HasJackBeamInBay(double zA, double zB, double mx)
            {
                if (!isJackPortalEnabled || !widthModuleOffsets.Any(x => Math.Abs(x - mx) < 0.05)) return false;
                double bayMin = Math.Min(zA, zB);
                double bayMax = Math.Max(zA, zB);
                // Find the enclosing intermediate columns interval [zLeft, zRight]
                double zLeft = jackColZList.Where(jz => jz <= bayMin + 0.01).DefaultIfEmpty(0.0).Max();
                double zRight = jackColZList.Where(jz => jz >= bayMax - 0.01).DefaultIfEmpty(totalLength).Min();

                // An interval [zLeft, zRight] has skipped frames if there exists a portal frame Zf with zLeft < Zf < zRight
                return zCoords.Any(zf => zf > zLeft + 0.05 && zf < zRight - 0.05);
            }

            // Helper to check whether an intermediate column at Z = z along X = mx connects to a Jack Beam
            bool HasJackBeamAtColumn(double z, double mx)
            {
                if (!isJackPortalEnabled || !widthModuleOffsets.Any(x => Math.Abs(x - mx) < 0.05)) return false;
                int idx = zCoords.FindIndex(zc => Math.Abs(zc - z) < 0.05);
                if (idx < 0)
                {
                    double zPrev = zCoords.Where(zc => zc > z + 0.01).DefaultIfEmpty(totalLength).Min();
                    double zNext = zCoords.Where(zc => zc < z - 0.01).DefaultIfEmpty(0.0).Max();
                    return HasJackBeamInBay(zPrev, zNext, mx);
                }

                bool leftBay = (idx > 0) && HasJackBeamInBay(zCoords[idx - 1], zCoords[idx], mx);
                bool rightBay = (idx < zCoords.Count - 1) && HasJackBeamInBay(zCoords[idx], zCoords[idx + 1], mx);
                return leftBay || rightBay;
            }

            // Maximum allowable roof cross brace diagonal length (default 12.5m as requested)
            double maxBraceLength = (config.Bracing != null && config.Bracing.MaxBraceLength > 0.0)
                ? config.Bracing.MaxBraceLength
                : 12.5;

            double maxBayZ = config.BaySpacings.Count > 0 ? config.BaySpacings.Max() : 6.0;

            // Pre-calculate distinct roof X positions per span (combining eaves, ridge, width modules, all gable posts, and ensuring brace diagonal <= 12.5m)
            var spanRoofXLookup = new List<List<double>>();
            double tempAccX = 0.0;
            for (int sIndex = 0; sIndex < config.Spans.Count; sIndex++)
            {
                var span = config.Spans[sIndex];
                double spanStartX = tempAccX;
                double spanEndX = tempAccX + span.SpanWidth;
                double ridgeX = spanStartX + (span.SpanWidth / 2.0);

                var xList = new List<double> { spanStartX, ridgeX, spanEndX };

                foreach (var mx in widthModuleOffsets)
                {
                    if (mx > spanStartX + 0.05 && mx < spanEndX - 0.05)
                        xList.Add(mx);
                }

                if (!isSingleMidFrame)
                {
                    foreach (var gx in frontPostOffsets)
                    {
                        if (gx > spanStartX + 0.05 && gx < spanEndX - 0.05)
                            xList.Add(gx);
                    }

                    foreach (var gx in rearPostOffsets)
                    {
                        if (gx > spanStartX + 0.05 && gx < spanEndX - 0.05)
                            xList.Add(gx);
                    }
                }

                var distinctX = new List<double>();
                foreach (var x in xList.OrderBy(v => v))
                {
                    if (!distinctX.Any(ex => Math.Abs(ex - x) < 0.05))
                    {
                        distinctX.Add(Math.Round(x, 4));
                    }
                }
                distinctX.Sort();

                var subdividedX = new List<double>();
                if (!isSingleMidFrame)
                {
                    // Enforce Max 12.5m Roof Cross Brace Length: Subdivide any panel where diagonal > 12.5m by inserting intermediate full-length strut lines
                    for (int i = 0; i < distinctX.Count - 1; i++)
                    {
                        double xA = distinctX[i];
                        double xB = distinctX[i + 1];
                        subdividedX.Add(xA);

                        double yA = CalculateSpanRafterY(xA, spanStartX, spanEndX, ridgeX, span.EaveHeightLeft, span.EaveHeightRight, span.RidgeHeight);
                        double yB = CalculateSpanRafterY(xB, spanStartX, spanEndX, ridgeX, span.EaveHeightLeft, span.EaveHeightRight, span.RidgeHeight);

                        double dx = xB - xA;
                        double dy = yB - yA;
                        double dz = maxBayZ;
                        double diagLen = Math.Sqrt(dx * dx + dy * dy + dz * dz);

                        if (diagLen > maxBraceLength)
                        {
                            int numDivisions = 2;
                            while (numDivisions < 10)
                            {
                                double subDx = dx / numDivisions;
                                double subDy = dy / numDivisions;
                                double subDiag = Math.Sqrt(subDx * subDx + subDy * subDy + dz * dz);
                                if (subDiag <= maxBraceLength)
                                    break;
                                numDivisions++;
                            }

                            for (int d = 1; d < numDivisions; d++)
                            {
                                double interX = Math.Round(xA + d * (dx / numDivisions), 4);
                                if (!subdividedX.Any(ex => Math.Abs(ex - interX) < 0.05))
                                {
                                    subdividedX.Add(interX);
                                }
                            }
                        }
                    }
                    subdividedX.Add(distinctX.Last());
                }
                else
                {
                    subdividedX = distinctX;
                }

                var finalDistinctX = new List<double>();
                foreach (var x in subdividedX.OrderBy(v => v))
                {
                    if (!finalDistinctX.Any(ex => Math.Abs(ex - x) < 0.05))
                    {
                        finalDistinctX.Add(Math.Round(x, 4));
                    }
                }
                finalDistinctX.Sort();

                spanRoofXLookup.Add(finalDistinctX);

                tempAccX += span.SpanWidth;
            }

            // Pre-calculate intermediate Y levels for side wall columns where wall bracing diagonal > maxBraceLength
            var frameLeftWallYLookup = new List<List<double>>();
            var frameRightWallYLookup = new List<List<double>>();
            for (int f = 0; f < zCoords.Count; f++)
            {
                frameLeftWallYLookup.Add(new List<double>());
                frameRightWallYLookup.Add(new List<double>());
            }

            if (config.Bracing != null && config.Bracing.IncludeWallBracing && config.Bracing.BracedBayIndices != null)
            {
                for (int b = 0; b < config.BaySpacings.Count; b++)
                {
                    bool isPortalBayLeft = config.Bracing.IsPortalBayLeft(b);
                    bool isPortalBayRight = config.Bracing.IsPortalBayRight(b);
                    bool isBracedBay = config.Bracing.BracedBayIndices.Contains(b);
                    if (!isBracedBay) continue;

                    bool isLeftCanopyBay = config.Canopy.LeftWall.IsInBay(b, config.BaySpacings.Count);
                    bool isRightCanopyBay = config.Canopy.RightWall.IsInBay(b, config.BaySpacings.Count);

                    double bayZ = config.BaySpacings[b];

                    // Left wall
                    if (!isPortalBayLeft && !isLeftCanopyBay)
                    {
                        double eaveHLeft = config.Spans.First().EaveHeightLeft;
                        double diagLeft = Math.Sqrt(eaveHLeft * eaveHLeft + bayZ * bayZ);
                        if (diagLeft > maxBraceLength)
                        {
                            int numDivisions = 2;
                            while (numDivisions < 10)
                            {
                                double subDy = eaveHLeft / numDivisions;
                                double subDiag = Math.Sqrt(subDy * subDy + bayZ * bayZ);
                                if (subDiag <= maxBraceLength)
                                    break;
                                numDivisions++;
                            }

                            for (int d = 1; d < numDivisions; d++)
                            {
                                double wy = Math.Round(d * (eaveHLeft / numDivisions), 4);
                                if (!frameLeftWallYLookup[b].Contains(wy)) frameLeftWallYLookup[b].Add(wy);
                                if (!frameLeftWallYLookup[b + 1].Contains(wy)) frameLeftWallYLookup[b + 1].Add(wy);
                            }
                        }
                    }

                    // Right wall
                    if (!isPortalBayRight && !isRightCanopyBay)
                    {
                        double eaveHRight = config.Spans.Last().EaveHeightRight;
                        double diagRight = Math.Sqrt(eaveHRight * eaveHRight + bayZ * bayZ);
                        if (diagRight > maxBraceLength)
                        {
                            int numDivisions = 2;
                            while (numDivisions < 10)
                            {
                                double subDy = eaveHRight / numDivisions;
                                double subDiag = Math.Sqrt(subDy * subDy + bayZ * bayZ);
                                if (subDiag <= maxBraceLength)
                                    break;
                                numDivisions++;
                            }

                            for (int d = 1; d < numDivisions; d++)
                            {
                                double wy = Math.Round(d * (eaveHRight / numDivisions), 4);
                                if (!frameRightWallYLookup[b].Contains(wy)) frameRightWallYLookup[b].Add(wy);
                                if (!frameRightWallYLookup[b + 1].Contains(wy)) frameRightWallYLookup[b + 1].Add(wy);
                            }
                        }
                    }
                }
            }

            // Add Portal Bracing intermediate Y levels (Portal beam height and upper subdivided tier levels)
            if (config.Bracing != null && config.Bracing.EnablePortalBracing)
            {
                for (int b = 0; b < config.BaySpacings.Count; b++)
                {
                    double bayZ = config.BaySpacings[b];

                    // Left Wall
                    if (config.Bracing.IsPortalBayLeft(b))
                    {
                        double eaveHLeft = config.Spans.First().EaveHeightLeft;
                        double portalHLeft = Math.Min(config.Bracing.PortalBracingHeight, eaveHLeft - 0.3);
                        if (portalHLeft < 1.0) portalHLeft = eaveHLeft * 0.65;
                        portalHLeft = Math.Round(portalHLeft, 4);

                        if (!frameLeftWallYLookup[b].Contains(portalHLeft)) frameLeftWallYLookup[b].Add(portalHLeft);
                        if (!frameLeftWallYLookup[b + 1].Contains(portalHLeft)) frameLeftWallYLookup[b + 1].Add(portalHLeft);

                        double upperDyLeft = eaveHLeft - portalHLeft;
                        double diagUpperLeft = Math.Sqrt(upperDyLeft * upperDyLeft + bayZ * bayZ);
                        int numDivUpperLeft = 1;
                        if (diagUpperLeft > maxBraceLength)
                        {
                            numDivUpperLeft = 2;
                            while (numDivUpperLeft < 10)
                            {
                                double subDy = upperDyLeft / numDivUpperLeft;
                                if (Math.Sqrt(subDy * subDy + bayZ * bayZ) <= maxBraceLength)
                                    break;
                                numDivUpperLeft++;
                            }
                        }
                        for (int d = 1; d < numDivUpperLeft; d++)
                        {
                            double wy = Math.Round(portalHLeft + d * (upperDyLeft / numDivUpperLeft), 4);
                            if (!frameLeftWallYLookup[b].Contains(wy)) frameLeftWallYLookup[b].Add(wy);
                            if (!frameLeftWallYLookup[b + 1].Contains(wy)) frameLeftWallYLookup[b + 1].Add(wy);
                        }
                    }

                    // Right Wall
                    if (config.Bracing.IsPortalBayRight(b))
                    {
                        double eaveHRight = config.Spans.Last().EaveHeightRight;
                        double portalHRight = Math.Min(config.Bracing.PortalBracingHeight, eaveHRight - 0.3);
                        if (portalHRight < 1.0) portalHRight = eaveHRight * 0.65;
                        portalHRight = Math.Round(portalHRight, 4);

                        if (!frameRightWallYLookup[b].Contains(portalHRight)) frameRightWallYLookup[b].Add(portalHRight);
                        if (!frameRightWallYLookup[b + 1].Contains(portalHRight)) frameRightWallYLookup[b + 1].Add(portalHRight);

                        double upperDyRight = eaveHRight - portalHRight;
                        double diagUpperRight = Math.Sqrt(upperDyRight * upperDyRight + bayZ * bayZ);
                        int numDivUpperRight = 1;
                        if (diagUpperRight > maxBraceLength)
                        {
                            numDivUpperRight = 2;
                            while (numDivUpperRight < 10)
                            {
                                double subDy = upperDyRight / numDivUpperRight;
                                if (Math.Sqrt(subDy * subDy + bayZ * bayZ) <= maxBraceLength)
                                    break;
                                numDivUpperRight++;
                            }
                        }
                        for (int d = 1; d < numDivUpperRight; d++)
                        {
                            double wy = Math.Round(portalHRight + d * (upperDyRight / numDivUpperRight), 4);
                            if (!frameRightWallYLookup[b].Contains(wy)) frameRightWallYLookup[b].Add(wy);
                            if (!frameRightWallYLookup[b + 1].Contains(wy)) frameRightWallYLookup[b + 1].Add(wy);
                        }
                    }
                }
            }

            // 1. Build Transverse Portal Frames at each Z coordinate
            for (int f = 0; f < zCoords.Count; f++)
            {
                double z = zCoords[f];
                var fRecord = new FrameRecord { Z = z };

                bool isGableFront = !isSingleMidFrame && (f == 0);
                bool isGableBack = !isSingleMidFrame && (f == zCoords.Count - 1);
                bool isInteriorFrame = isSingleMidFrame || (!isGableFront && !isGableBack);

                double accumulatedX = 0.0;
                var currentGableOffsets = isSingleMidFrame ? new List<double>() : (isGableFront ? frontPostOffsets : (isGableBack ? rearPostOffsets : new List<double>()));

                for (int sIndex = 0; sIndex < config.Spans.Count; sIndex++)
                {
                    var span = config.Spans[sIndex];
                    double spanStartX = accumulatedX;
                    double spanEndX = accumulatedX + span.SpanWidth;
                    double ridgeX = spanStartX + (span.SpanWidth / 2.0);
                    double ridgeY = span.RidgeHeight;

                    bool isExteriorLeft = (sIndex == 0);
                    bool isExteriorRight = (sIndex == config.Spans.Count - 1);

                    var distinctRoofX = spanRoofXLookup[sIndex];

                    // Build Rafter Nodes for all strut / post / module positions on this frame
                    var spanRafterNodes = new List<Node3D>();
                    foreach (var rx in distinctRoofX)
                    {
                        double ry = CalculateSpanRafterY(rx, spanStartX, spanEndX, ridgeX, span.EaveHeightLeft, span.EaveHeightRight, ridgeY);
                        var rNode = AddNode(rx, ry, z);
                        spanRafterNodes.Add(rNode);
                    }

                    // BREAK RAFTER AT EACH NODE LOCATION: Connect adjacent node segments along rafter
                    for (int i = 0; i < spanRafterNodes.Count - 1; i++)
                    {
                        AddBeam(spanRafterNodes[i].Id, spanRafterNodes[i + 1].Id, MemberType.Rafter);
                    }

                    var colEaveLeft = spanRafterNodes.First();
                    var colEaveRight = spanRafterNodes.Last();
                    var ridgeNode = spanRafterNodes.FirstOrDefault(n => Math.Abs(n.X - ridgeX) < 0.05) ?? spanRafterNodes[spanRafterNodes.Count / 2];

                    // --- Left Column of this Span ---
                    var colBaseLeft = AddNode(spanStartX, 0.0, z);
                    if (isExteriorLeft)
                        AddFixedBaseNode(colBaseLeft);
                    else
                        AddPinnedBaseNode(colBaseLeft);

                    Node3D? rccNodeLeft = null;
                    if (isExteriorLeft && config.RccWallHeight > 0.001 && config.RccWallHeight < span.EaveHeightLeft)
                    {
                        rccNodeLeft = AddNode(spanStartX, config.RccWallHeight, z);
                        fRecord.RccWallNodeLeft = rccNodeLeft;
                    }

                    Node3D? cableTrayTipLeft = null;
                    Node3D? cableTrayColLeft = null;
                    if (includeSide && isExteriorLeft)
                    {
                        cableTrayColLeft = AddNode(spanStartX, config.CableTray!.BracketHeight, z);
                        cableTrayTipLeft = AddNode(spanStartX + config.CableTray.BracketLength, config.CableTray.BracketHeight, z);
                        AddBeam(cableTrayColLeft.Id, cableTrayTipLeft.Id, MemberType.CableTrayBracket);
                    }

                    // --- Left Canopy Generation on Column ---
                    Node3D? leftCanopyColNode = null;
                    Node3D? leftCanopyTipNode = null;
                    bool hasLeftCanopyOnFrame = config.Canopy.LeftWall.Enabled &&
                        (isSingleMidFrame ||
                         (f > 0 && config.Canopy.LeftWall.IsInBay(f - 1, config.BaySpacings.Count)) ||
                         (f < config.BaySpacings.Count && config.Canopy.LeftWall.IsInBay(f, config.BaySpacings.Count)));

                    if (isExteriorLeft && hasLeftCanopyOnFrame && config.Canopy.LeftWall.Height > 0.1 && config.Canopy.LeftWall.Height < span.EaveHeightLeft)
                    {
                        leftCanopyColNode = AddNode(spanStartX, config.Canopy.LeftWall.Height, z);
                        leftCanopyTipNode = AddNode(spanStartX - config.Canopy.LeftWall.Projection, config.Canopy.LeftWall.Height - config.Canopy.LeftWall.Drop, z);
                        AddBeam(leftCanopyColNode.Id, leftCanopyTipNode.Id, MemberType.CanopyRafter, "CANOPY_RAFTER");
                        fRecord.LeftCanopyNode = leftCanopyColNode;
                        fRecord.LeftCanopyTip = leftCanopyTipNode;
                    }

                    // BREAK LEFT COLUMN AT EACH NODE LOCATION (Base -> Wall Node -> Cable Tray Node -> Canopy Node -> Wall Bracing Strut Node -> Eave)
                    if (isExteriorLeft || isInteriorFrame)
                    {
                        var leftKeyNodes = new List<Node3D> { colBaseLeft, colEaveLeft };
                        if (rccNodeLeft != null) leftKeyNodes.Add(rccNodeLeft);
                        if (cableTrayColLeft != null) leftKeyNodes.Add(cableTrayColLeft);
                        if (leftCanopyColNode != null) leftKeyNodes.Add(leftCanopyColNode);

                        if (isExteriorLeft && isGableFront && config.Canopy.FrontWall.Enabled && config.Canopy.FrontWall.Height < span.EaveHeightLeft)
                        {
                            var frontColX = frontPostOffsets.Where(x => x > 0.05 && x < totalWidth - 0.05).Distinct().OrderBy(x => x).ToList();
                            frontColX.Insert(0, 0.0);
                            frontColX.Add(totalWidth);
                            int numFrontBays = frontColX.Count - 1;
                            if (config.Canopy.FrontWall.IsInBay(0, numFrontBays))
                            {
                                leftKeyNodes.Add(AddNode(spanStartX, config.Canopy.FrontWall.Height, z));
                            }
                        }
                        if (isExteriorLeft && isGableBack && config.Canopy.RearWall.Enabled && config.Canopy.RearWall.Height < span.EaveHeightLeft)
                        {
                            var rearColX = rearPostOffsets.Where(x => x > 0.05 && x < totalWidth - 0.05).Distinct().OrderBy(x => x).ToList();
                            rearColX.Insert(0, 0.0);
                            rearColX.Add(totalWidth);
                            int numRearBays = rearColX.Count - 1;
                            if (config.Canopy.RearWall.IsInBay(0, numRearBays))
                            {
                                leftKeyNodes.Add(AddNode(spanStartX, config.Canopy.RearWall.Height, z));
                            }
                        }

                        if (isExteriorLeft)
                        {
                            foreach (var wy in frameLeftWallYLookup[f])
                            {
                                leftKeyNodes.Add(AddNode(spanStartX, wy, z));
                            }
                        }

                        leftKeyNodes = leftKeyNodes.OrderBy(n => n.Y).Distinct().ToList();

                        for (int i = 0; i < leftKeyNodes.Count - 1; i++)
                        {
                            AddBeam(leftKeyNodes[i].Id, leftKeyNodes[i + 1].Id, isExteriorLeft ? MemberType.Column : MemberType.IntermediateColumn);
                        }
                    }

                    // --- Right Column of this Span ---
                    var colBaseRight = AddNode(spanEndX, 0.0, z);
                    if (isExteriorRight)
                        AddFixedBaseNode(colBaseRight);
                    else
                        AddPinnedBaseNode(colBaseRight);

                    Node3D? rccNodeRight = null;
                    if (isExteriorRight && config.RccWallHeight > 0.001 && config.RccWallHeight < span.EaveHeightRight)
                    {
                        rccNodeRight = AddNode(spanEndX, config.RccWallHeight, z);
                        fRecord.RccWallNodeRight = rccNodeRight;
                    }

                    Node3D? cableTrayTipRight = null;
                    Node3D? cableTrayColRight = null;
                    if (includeSide && isExteriorRight)
                    {
                        cableTrayColRight = AddNode(spanEndX, config.CableTray!.BracketHeight, z);
                        cableTrayTipRight = AddNode(spanEndX - config.CableTray.BracketLength, config.CableTray.BracketHeight, z);
                        AddBeam(cableTrayColRight.Id, cableTrayTipRight.Id, MemberType.CableTrayBracket);
                    }

                    // --- Right Canopy Generation on Column ---
                    Node3D? rightCanopyColNode = null;
                    Node3D? rightCanopyTipNode = null;
                    bool hasRightCanopyOnFrame = config.Canopy.RightWall.Enabled &&
                        (isSingleMidFrame ||
                         (f > 0 && config.Canopy.RightWall.IsInBay(f - 1, config.BaySpacings.Count)) ||
                         (f < config.BaySpacings.Count && config.Canopy.RightWall.IsInBay(f, config.BaySpacings.Count)));

                    if (isExteriorRight && hasRightCanopyOnFrame && config.Canopy.RightWall.Height > 0.1 && config.Canopy.RightWall.Height < span.EaveHeightRight)
                    {
                        rightCanopyColNode = AddNode(spanEndX, config.Canopy.RightWall.Height, z);
                        rightCanopyTipNode = AddNode(spanEndX + config.Canopy.RightWall.Projection, config.Canopy.RightWall.Height - config.Canopy.RightWall.Drop, z);
                        AddBeam(rightCanopyColNode.Id, rightCanopyTipNode.Id, MemberType.CanopyRafter, "CANOPY_RAFTER");
                        fRecord.RightCanopyNode = rightCanopyColNode;
                        fRecord.RightCanopyTip = rightCanopyTipNode;
                    }

                    // BREAK RIGHT COLUMN AT EACH NODE LOCATION (Base -> Wall Node -> Cable Tray Node -> Canopy Node -> Wall Bracing Strut Node -> Eave)
                    if (isExteriorRight)
                    {
                        var rightKeyNodes = new List<Node3D> { colBaseRight, colEaveRight };
                        if (rccNodeRight != null) rightKeyNodes.Add(rccNodeRight);
                        if (cableTrayColRight != null) rightKeyNodes.Add(cableTrayColRight);
                        if (rightCanopyColNode != null) rightKeyNodes.Add(rightCanopyColNode);

                        if (isExteriorRight && isGableFront && config.Canopy.FrontWall.Enabled && config.Canopy.FrontWall.Height < span.EaveHeightRight)
                        {
                            var frontColX = frontPostOffsets.Where(x => x > 0.05 && x < totalWidth - 0.05).Distinct().OrderBy(x => x).ToList();
                            frontColX.Insert(0, 0.0);
                            frontColX.Add(totalWidth);
                            int numFrontBays = frontColX.Count - 1;
                            if (config.Canopy.FrontWall.IsInBay(numFrontBays - 1, numFrontBays))
                            {
                                rightKeyNodes.Add(AddNode(spanEndX, config.Canopy.FrontWall.Height, z));
                            }
                        }
                        if (isExteriorRight && isGableBack && config.Canopy.RearWall.Enabled && config.Canopy.RearWall.Height < span.EaveHeightRight)
                        {
                            var rearColX = rearPostOffsets.Where(x => x > 0.05 && x < totalWidth - 0.05).Distinct().OrderBy(x => x).ToList();
                            rearColX.Insert(0, 0.0);
                            rearColX.Add(totalWidth);
                            int numRearBays = rearColX.Count - 1;
                            if (config.Canopy.RearWall.IsInBay(numRearBays - 1, numRearBays))
                            {
                                rightKeyNodes.Add(AddNode(spanEndX, config.Canopy.RearWall.Height, z));
                            }
                        }

                        foreach (var wy in frameRightWallYLookup[f])
                        {
                            rightKeyNodes.Add(AddNode(spanEndX, wy, z));
                        }

                        rightKeyNodes = rightKeyNodes.OrderBy(n => n.Y).Distinct().ToList();

                        for (int i = 0; i < rightKeyNodes.Count - 1; i++)
                        {
                            AddBeam(rightKeyNodes[i].Id, rightKeyNodes[i + 1].Id, MemberType.Column);
                        }
                    }

                    // Add Width Module Intermediate Columns (ONLY on Interior Frames where column is placed, NO wall node!)
                    // BREAK INTERMEDIATE COLUMN AT EACH NODE LOCATION (Base -> Cable Tray Node -> Tie Beam Node 1m Below Rafter -> Rafter Top)
                    // If Jack Portal is enabled and intermediate frame has no column to ground, add vertical post from Jack Beam (1m below rafter) to rafter top
                    if (isInteriorFrame && widthModuleOffsets.Count > 0)
                    {
                        var spanModOffsets = widthModuleOffsets.Where(mx => mx > spanStartX + 0.05 && mx < spanEndX - 0.05).ToList();
                        foreach (var mx in spanModOffsets)
                        {
                            var topMod = spanRafterNodes.FirstOrDefault(n => Math.Abs(n.X - mx) < 0.05);
                            if (topMod == null) continue;

                            bool hasColumnHere = isSingleMidFrame || !isJackPortalEnabled || jackColZList.Any(jz => Math.Abs(jz - z) < 0.05);

                            if (hasColumnHere)
                            {
                                var baseMod = AddNode(mx, 0.0, z);
                                AddPinnedBaseNode(baseMod);
                                var modKeyNodes = new List<Node3D> { baseMod, topMod };

                                if (includeIntermediate && config.CableTray!.BracketHeight < topMod.Y)
                                {
                                    var colModCable = AddNode(mx, config.CableTray.BracketHeight, z);
                                    var tipModCable = AddNode(mx + config.CableTray.BracketLength, config.CableTray.BracketHeight, z);
                                    AddBeam(colModCable.Id, tipModCable.Id, MemberType.CableTrayBracket);
                                    modKeyNodes.Add(colModCable);
                                }

                                bool hasJackConnecting = !isSingleMidFrame && HasJackBeamAtColumn(z, mx);
                                if (hasJackConnecting)
                                {
                                    double tieY = Math.Round(Math.Max(0.5, topMod.Y - 1.0), 4);
                                    var tieNode = AddNode(mx, tieY, z);
                                    modKeyNodes.Add(tieNode);
                                }

                                modKeyNodes = modKeyNodes.OrderBy(n => n.Y).Distinct().ToList();
                                for (int i = 0; i < modKeyNodes.Count - 1; i++)
                                {
                                    var colBeam = AddBeam(modKeyNodes[i].Id, modKeyNodes[i + 1].Id, MemberType.IntermediateColumn);
                                    if (colBeam != null && hasJackConnecting)
                                    {
                                        // Rotate column by Beta 90 when Jack Portal is modeled (moment connection in longitudinal direction)
                                        _beta90BeamIds.Add(colBeam.Id);

                                        if (i == modKeyNodes.Count - 2)
                                        {
                                            // Release My Mz at top connecting to rafter (ONLY if column is part of Jack Portal)
                                            _endReleaseMyMzBeamIds.Add(colBeam.Id);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // Skipped intermediate frame: vertical post connecting Jack Beam (1m below rafter) up to Rafter
                                double tieY = Math.Round(Math.Max(0.5, topMod.Y - 1.0), 4);
                                var tieNode = AddNode(mx, tieY, z);
                                var stubBeam = AddBeam(tieNode.Id, topMod.Id, MemberType.IntermediateColumn, "JACK_POST");
                                if (stubBeam != null)
                                {
                                    // Rotate stub post by Beta 90
                                    _beta90BeamIds.Add(stubBeam.Id);

                                    // Release My Mz at BOTH bottom (Start) and top (End)
                                    _startReleaseMyMzBeamIds.Add(stubBeam.Id);
                                    _endReleaseMyMzBeamIds.Add(stubBeam.Id);
                                }
                            }
                        }
                    }

                    // Add Gable Wind Posts (ONLY on Front & Rear Endwalls)
                    // BREAK GABLE POST AT EACH NODE LOCATION (Base -> Wall Node -> Cable Tray Node -> Tie Beam Node -> Rafter Top)
                    if (isGableFront || isGableBack)
                    {
                        var spanPosts = currentGableOffsets.Where(gx => gx > spanStartX + 0.05 && gx < spanEndX - 0.05).ToList();
                        foreach (var gx in spanPosts)
                        {
                            var topGable = spanRafterNodes.FirstOrDefault(n => Math.Abs(n.X - gx) < 0.05);
                            if (topGable == null) continue;

                            var baseGable = AddNode(gx, 0.0, z);
                            AddPinnedBaseNode(baseGable);
                            var keyGableNodes = new List<Node3D> { baseGable, topGable };

                            if (config.RccWallHeight > 0.001 && config.RccWallHeight < topGable.Y)
                            {
                                var wallNode = AddNode(gx, config.RccWallHeight, z);
                                keyGableNodes.Add(wallNode);
                            }

                            if (includeGable && config.CableTray!.BracketHeight < topGable.Y)
                            {
                                var colGableCable = AddNode(gx, config.CableTray.BracketHeight, z);
                                var tipGableCable = AddNode(gx + config.CableTray.BracketLength, config.CableTray.BracketHeight, z);
                                AddBeam(colGableCable.Id, tipGableCable.Id, MemberType.CableTrayBracket);
                                keyGableNodes.Add(colGableCable);
                            }

                            // ONLY break column if a Jack Beam is actually connected to it!
                            bool hasJackConnecting = !isSingleMidFrame && HasJackBeamAtColumn(z, gx);
                            if (hasJackConnecting)
                            {
                                double tieY = Math.Round(Math.Max(0.5, topGable.Y - 1.0), 4);
                                var tieNode = AddNode(gx, tieY, z);
                                keyGableNodes.Add(tieNode);
                            }

                            // Break gable post at Canopy height if Front/Rear canopy is active on this post
                            if (isGableFront && config.Canopy.FrontWall.Enabled && config.Canopy.FrontWall.Height < topGable.Y - 0.2)
                            {
                                var frontColX = frontPostOffsets.Where(x => x > 0.05 && x < totalWidth - 0.05).Distinct().OrderBy(x => x).ToList();
                                frontColX.Insert(0, 0.0);
                                frontColX.Add(totalWidth);
                                int numFrontBays = frontColX.Count - 1;
                                int pIdx = frontColX.FindIndex(x => Math.Abs(x - gx) < 0.05);
                                bool hasCanopy = (pIdx > 0 && config.Canopy.FrontWall.IsInBay(pIdx - 1, numFrontBays)) ||
                                                 (pIdx >= 0 && pIdx < numFrontBays && config.Canopy.FrontWall.IsInBay(pIdx, numFrontBays));
                                if (hasCanopy)
                                {
                                    var canNode = AddNode(gx, config.Canopy.FrontWall.Height, z);
                                    keyGableNodes.Add(canNode);
                                }
                            }
                            if (isGableBack && config.Canopy.RearWall.Enabled && config.Canopy.RearWall.Height < topGable.Y - 0.2)
                            {
                                var rearColX = rearPostOffsets.Where(x => x > 0.05 && x < totalWidth - 0.05).Distinct().OrderBy(x => x).ToList();
                                rearColX.Insert(0, 0.0);
                                rearColX.Add(totalWidth);
                                int numRearBays = rearColX.Count - 1;
                                int pIdx = rearColX.FindIndex(x => Math.Abs(x - gx) < 0.05);
                                bool hasCanopy = (pIdx > 0 && config.Canopy.RearWall.IsInBay(pIdx - 1, numRearBays)) ||
                                                 (pIdx >= 0 && pIdx < numRearBays && config.Canopy.RearWall.IsInBay(pIdx, numRearBays));
                                if (hasCanopy)
                                {
                                    var canNode = AddNode(gx, config.Canopy.RearWall.Height, z);
                                    keyGableNodes.Add(canNode);
                                }
                            }

                            keyGableNodes = keyGableNodes.OrderBy(n => n.Y).Distinct().ToList();
                            for (int i = 0; i < keyGableNodes.Count - 1; i++)
                            {
                                var gb = AddBeam(keyGableNodes[i].Id, keyGableNodes[i + 1].Id, MemberType.GablePost);
                                if (gb != null)
                                {
                                    _beta90BeamIds.Add(gb.Id);
                                    if (i == keyGableNodes.Count - 2)
                                    {
                                        // Release My Mz at top of Gable Post connecting to rafter
                                        _endReleaseMyMzBeamIds.Add(gb.Id);
                                    }
                                }
                            }
                        }
                    }

                    var spanRecord = new SpanRecord
                    {
                        BaseLeft = colBaseLeft,
                        EaveLeft = colEaveLeft,
                        Ridge = ridgeNode,
                        EaveRight = colEaveRight,
                        BaseRight = colBaseRight,
                        RafterNodes = spanRafterNodes,
                        CableTrayTipLeft = cableTrayTipLeft,
                        CableTrayTipRight = cableTrayTipRight
                    };

                    fRecord.Spans.Add(spanRecord);
                    accumulatedX += span.SpanWidth;
                }

                frameRecords.Add(fRecord);
            }

            // 2. Build 3D Longitudinal Members (Eave/Ridge/Gable Strut Pipes, Jack Beams 1m below rafter, Subdivided Roof Bracings, Wall Bracings, Mezzanines)
            for (int b = 0; b < config.BaySpacings.Count; b++)
            {
                var currFrame = frameRecords[b];
                var nextFrame = frameRecords[b + 1];

                for (int sIndex = 0; sIndex < config.Spans.Count; sIndex++)
                {
                    var currSpan = currFrame.Spans[sIndex];
                    var nextSpan = nextFrame.Spans[sIndex];

                    // Longitudinal Struts along all roof lines (Eaves, Ridge, and all Gable Post / Width Module lines from Front to Rear)
                    for (int k = 0; k < currSpan.RafterNodes.Count; k++)
                    {
                        var nodeA = currSpan.RafterNodes[k];
                        var nodeB = nextSpan.RafterNodes[k];

                        MemberType strutType;
                        if (k == 0 || k == currSpan.RafterNodes.Count - 1)
                        {
                            strutType = MemberType.EaveStrut;
                        }
                        else if (Math.Abs(nodeA.X - currSpan.Ridge.X) < 0.01)
                        {
                            strutType = MemberType.RidgeStrut;
                        }
                        else
                        {
                            strutType = MemberType.EaveStrut; // Longitudinal Strut Pipe along gable post line
                        }

                        // Standard roof strut pipe at rafter level
                        AddBeam(nodeA.Id, nodeB.Id, strutType);

                        // Jack Portal: Horizontal Jack Beam placed 1m below rafter level (ONLY when intermediate rafters are skipped)
                        if (HasJackBeamInBay(currFrame.Z, nextFrame.Z, nodeA.X))
                        {
                            double tieYA = Math.Round(Math.Max(0.5, nodeA.Y - 1.0), 4);
                            double tieYB = Math.Round(Math.Max(0.5, nodeB.Y - 1.0), 4);
                            var tieNodeA = AddNode(nodeA.X, tieYA, currFrame.Z);
                            var tieNodeB = AddNode(nodeB.X, tieYB, nextFrame.Z);
                            AddBeam(tieNodeA.Id, tieNodeB.Id, MemberType.JackBeam);
                        }
                    }

                    // Subdivided Roof X-Bracing across each panel between adjacent longitudinal struts
                    if (config.Bracing != null && config.Bracing.IncludeRoofBracing && config.Bracing.BracedBayIndices != null && config.Bracing.BracedBayIndices.Contains(b))
                    {
                        for (int k = 0; k < currSpan.RafterNodes.Count - 1; k++)
                        {
                            var n1 = currSpan.RafterNodes[k];
                            var n2 = currSpan.RafterNodes[k + 1];
                            var n3 = nextSpan.RafterNodes[k];
                            var n4 = nextSpan.RafterNodes[k + 1];

                            AddBeam(n1.Id, n4.Id, MemberType.RoofBracing);
                            AddBeam(n2.Id, n3.Id, MemberType.RoofBracing);
                        }
                    }
                }

                bool isPortalBayLeft = config.Bracing?.IsPortalBayLeft(b) ?? false;
                bool isPortalBayRight = config.Bracing?.IsPortalBayRight(b) ?? false;

                // Wall X-Bracing (Only for non-portal braced bays, Subdivided if diagonal > maxBraceLength with intermediate wall strut in braced bay ONLY)
                if (config.Bracing != null && config.Bracing.IncludeWallBracing && config.Bracing.BracedBayIndices != null && config.Bracing.BracedBayIndices.Contains(b))
                {
                    double bayZ = config.BaySpacings[b];

                    // --- Left Wall Bracing (X = 0) ---
                    if (!isPortalBayLeft)
                    {
                        if (config.Canopy.LeftWall.IsInBay(b, config.BaySpacings.Count) && currFrame.LeftCanopyNode != null && nextFrame.LeftCanopyNode != null)
                        {
                            var nBaseA = currFrame.Spans.First().BaseLeft;
                            var nBaseB = nextFrame.Spans.First().BaseLeft;
                            var nEaveA = currFrame.Spans.First().EaveLeft;
                            var nEaveB = nextFrame.Spans.First().EaveLeft;

                            AddWallXBracingPanel(nBaseA, currFrame.LeftCanopyNode, nBaseB, nextFrame.LeftCanopyNode, 0.0, bayZ, maxBraceLength);
                            if (nEaveA.Y > currFrame.LeftCanopyNode.Y + 0.3)
                            {
                                AddWallXBracingPanel(currFrame.LeftCanopyNode, nEaveA, nextFrame.LeftCanopyNode, nEaveB, 0.0, bayZ, maxBraceLength);
                            }
                        }
                        else
                        {
                            double eaveHLeft = config.Spans.First().EaveHeightLeft;
                            double diagLeft = Math.Sqrt(eaveHLeft * eaveHLeft + bayZ * bayZ);

                            int numDivLeft = 1;
                            if (diagLeft > maxBraceLength)
                            {
                                numDivLeft = 2;
                                while (numDivLeft < 10)
                                {
                                    double subDy = eaveHLeft / numDivLeft;
                                    if (Math.Sqrt(subDy * subDy + bayZ * bayZ) <= maxBraceLength)
                                        break;
                                    numDivLeft++;
                                }
                            }

                            // Intermediate wall strut pipes (ONLY within this braced bay)
                            for (int d = 1; d < numDivLeft; d++)
                            {
                                double wy = Math.Round(d * (eaveHLeft / numDivLeft), 4);
                                var sNodeA = AddNode(0.0, wy, currFrame.Z);
                                var sNodeB = AddNode(0.0, wy, nextFrame.Z);
                                AddBeam(sNodeA.Id, sNodeB.Id, MemberType.EaveStrut, "WALL_STRUT");
                            }

                            // Tiered Wall Cross-Bracing
                            for (int d = 0; d < numDivLeft; d++)
                            {
                                double yBottom = Math.Round(d * (eaveHLeft / numDivLeft), 4);
                                double yTop = Math.Round((d + 1) * (eaveHLeft / numDivLeft), 4);

                                var n1 = AddNode(0.0, yBottom, currFrame.Z);
                                var n2 = AddNode(0.0, yTop, currFrame.Z);
                                var n3 = AddNode(0.0, yBottom, nextFrame.Z);
                                var n4 = AddNode(0.0, yTop, nextFrame.Z);

                                if (config.Bracing.Type == BracingType.XBrace)
                                {
                                    AddBeam(n1.Id, n4.Id, MemberType.WallBracing);
                                    AddBeam(n2.Id, n3.Id, MemberType.WallBracing);
                                }
                                else if (config.Bracing.Type == BracingType.InvertedV)
                                {
                                    double midZ = (currFrame.Z + nextFrame.Z) / 2.0;
                                    var midTop = AddNode(0.0, yTop, midZ);
                                    AddBeam(n1.Id, midTop.Id, MemberType.WallBracing);
                                    AddBeam(n3.Id, midTop.Id, MemberType.WallBracing);
                                }
                            }
                        }
                    }

                    // --- Right Wall Bracing (X = totalWidth) ---
                    if (!isPortalBayRight)
                    {
                        if (config.Canopy.RightWall.IsInBay(b, config.BaySpacings.Count) && currFrame.RightCanopyNode != null && nextFrame.RightCanopyNode != null)
                        {
                            var nBaseA = currFrame.Spans.Last().BaseRight;
                            var nBaseB = nextFrame.Spans.Last().BaseRight;
                            var nEaveA = currFrame.Spans.Last().EaveRight;
                            var nEaveB = nextFrame.Spans.Last().EaveRight;

                            AddWallXBracingPanel(nBaseA, currFrame.RightCanopyNode, nBaseB, nextFrame.RightCanopyNode, totalWidth, bayZ, maxBraceLength);
                            if (nEaveA.Y > currFrame.RightCanopyNode.Y + 0.3)
                            {
                                AddWallXBracingPanel(currFrame.RightCanopyNode, nEaveA, nextFrame.RightCanopyNode, nEaveB, totalWidth, bayZ, maxBraceLength);
                            }
                        }
                        else
                        {
                            double eaveHRight = config.Spans.Last().EaveHeightRight;
                            double diagRight = Math.Sqrt(eaveHRight * eaveHRight + bayZ * bayZ);

                            int numDivRight = 1;
                            if (diagRight > maxBraceLength)
                            {
                                numDivRight = 2;
                                while (numDivRight < 10)
                                {
                                    double subDy = eaveHRight / numDivRight;
                                    if (Math.Sqrt(subDy * subDy + bayZ * bayZ) <= maxBraceLength)
                                        break;
                                    numDivRight++;
                                }
                            }

                            // Intermediate wall strut pipes (ONLY within this braced bay)
                            for (int d = 1; d < numDivRight; d++)
                            {
                                double wy = Math.Round(d * (eaveHRight / numDivRight), 4);
                                var sNodeA = AddNode(totalWidth, wy, currFrame.Z);
                                var sNodeB = AddNode(totalWidth, wy, nextFrame.Z);
                                AddBeam(sNodeA.Id, sNodeB.Id, MemberType.EaveStrut, "WALL_STRUT");
                            }

                            // Tiered Wall Cross-Bracing
                            for (int d = 0; d < numDivRight; d++)
                            {
                                double yBottom = Math.Round(d * (eaveHRight / numDivRight), 4);
                                double yTop = Math.Round((d + 1) * (eaveHRight / numDivRight), 4);

                                var n1 = AddNode(totalWidth, yBottom, currFrame.Z);
                                var n2 = AddNode(totalWidth, yTop, currFrame.Z);
                                var n3 = AddNode(totalWidth, yBottom, nextFrame.Z);
                                var n4 = AddNode(totalWidth, yTop, nextFrame.Z);

                                if (config.Bracing.Type == BracingType.XBrace)
                                {
                                    AddBeam(n1.Id, n4.Id, MemberType.WallBracing);
                                    AddBeam(n2.Id, n3.Id, MemberType.WallBracing);
                                }
                                else if (config.Bracing.Type == BracingType.InvertedV)
                                {
                                    double midZ = (currFrame.Z + nextFrame.Z) / 2.0;
                                    var midTop = AddNode(totalWidth, yTop, midZ);
                                    AddBeam(n1.Id, midTop.Id, MemberType.WallBracing);
                                    AddBeam(n3.Id, midTop.Id, MemberType.WallBracing);
                                }
                            }
                        }
                    }
                }

                // Portal Bracing (Header Beam with inner 0.5m offset nodes, Tapered Legs to Base, and Subdivided Upper X-Bracing above)
                double bayLength = config.BaySpacings[b];
                double legOffset = config.Bracing!.PortalLegOffset > 0.01 ? config.Bracing.PortalLegOffset : 0.5;
                legOffset = Math.Min(legOffset, bayLength * 0.35);

                // --- Left Wall Portal Bracing (X = 0) ---
                if (isPortalBayLeft)
                {
                    double eaveHLeft = config.Spans.First().EaveHeightLeft;
                    double portalHLeft = Math.Min(config.Bracing.PortalBracingHeight, eaveHLeft - 0.3);
                    if (portalHLeft < 1.0) portalHLeft = eaveHLeft * 0.65;
                    portalHLeft = Math.Round(portalHLeft, 4);

                    var pCol1Top = AddNode(0.0, portalHLeft, currFrame.Z);
                    var pCol2Top = AddNode(0.0, portalHLeft, nextFrame.Z);
                    var pInner1 = AddNode(0.0, portalHLeft, currFrame.Z - legOffset);
                    var pInner2 = AddNode(0.0, portalHLeft, nextFrame.Z + legOffset);
                    var pBase1 = AddNode(0.0, 0.0, currFrame.Z);
                    var pBase2 = AddNode(0.0, 0.0, nextFrame.Z);

                    // 1. Horizontal Portal Header Beam (3 segments)
                    var pb1 = AddBeam(pCol1Top.Id, pInner1.Id, MemberType.PortalBeam);
                    var pb2 = AddBeam(pInner1.Id, pInner2.Id, MemberType.PortalBeam);
                    var pb3 = AddBeam(pInner2.Id, pCol2Top.Id, MemberType.PortalBeam);

                    if (pb1 != null) _startReleaseMyMzBeamIds.Add(pb1.Id); // Released at column 1 connection
                    if (pb3 != null) _endReleaseMyMzBeamIds.Add(pb3.Id);   // Released at column 2 connection

                    // 2. Inclined Portal Legs / Cross Lugs (connecting strut member at 0.5m offset to column base)
                    var leg1 = AddBeam(pInner1.Id, pBase1.Id, MemberType.PortalKneeBrace, "PORTAL_LEG");
                    var leg2 = AddBeam(pInner2.Id, pBase2.Id, MemberType.PortalKneeBrace, "PORTAL_LEG");

                    if (leg1 != null) _endReleaseMyMzBeamIds.Add(leg1.Id); // Released at base
                    if (leg2 != null) _endReleaseMyMzBeamIds.Add(leg2.Id); // Released at base

                    // 3. Subdivided Upper Wall Cross-Bracing above Portal Beam
                    double upperDyLeft = eaveHLeft - portalHLeft;
                    double diagUpperLeft = Math.Sqrt(upperDyLeft * upperDyLeft + bayLength * bayLength);
                    int numDivUpperLeft = 1;
                    if (diagUpperLeft > maxBraceLength)
                    {
                        numDivUpperLeft = 2;
                        while (numDivUpperLeft < 10)
                        {
                            double subDy = upperDyLeft / numDivUpperLeft;
                            if (Math.Sqrt(subDy * subDy + bayLength * bayLength) <= maxBraceLength)
                                break;
                            numDivUpperLeft++;
                        }
                    }

                    // Intermediate wall strut pipes in upper portal region
                    for (int d = 1; d < numDivUpperLeft; d++)
                    {
                        double wy = Math.Round(portalHLeft + d * (upperDyLeft / numDivUpperLeft), 4);
                        var sNodeA = AddNode(0.0, wy, currFrame.Z);
                        var sNodeB = AddNode(0.0, wy, nextFrame.Z);
                        AddBeam(sNodeA.Id, sNodeB.Id, MemberType.EaveStrut, "WALL_STRUT");
                    }

                    // Tiered Upper Wall Cross-Bracing
                    for (int d = 0; d < numDivUpperLeft; d++)
                    {
                        double yBottom = Math.Round(portalHLeft + d * (upperDyLeft / numDivUpperLeft), 4);
                        double yTop = Math.Round(portalHLeft + (d + 1) * (upperDyLeft / numDivUpperLeft), 4);

                        var n1 = AddNode(0.0, yBottom, currFrame.Z);
                        var n2 = AddNode(0.0, yTop, currFrame.Z);
                        var n3 = AddNode(0.0, yBottom, nextFrame.Z);
                        var n4 = AddNode(0.0, yTop, nextFrame.Z);

                        AddBeam(n1.Id, n4.Id, MemberType.WallBracing);
                        AddBeam(n2.Id, n3.Id, MemberType.WallBracing);
                    }
                }

                // --- Right Wall Portal Bracing (X = totalWidth) ---
                if (isPortalBayRight)
                {
                    double eaveHRight = config.Spans.Last().EaveHeightRight;
                    double portalHRight = Math.Min(config.Bracing.PortalBracingHeight, eaveHRight - 0.3);
                    if (portalHRight < 1.0) portalHRight = eaveHRight * 0.65;
                    portalHRight = Math.Round(portalHRight, 4);

                    var pCol1TopR = AddNode(totalWidth, portalHRight, currFrame.Z);
                    var pCol2TopR = AddNode(totalWidth, portalHRight, nextFrame.Z);
                    var pInner1R = AddNode(totalWidth, portalHRight, currFrame.Z - legOffset);
                    var pInner2R = AddNode(totalWidth, portalHRight, nextFrame.Z + legOffset);
                    var pBase1R = AddNode(totalWidth, 0.0, currFrame.Z);
                    var pBase2R = AddNode(totalWidth, 0.0, nextFrame.Z);

                    // 1. Horizontal Portal Header Beam (3 segments)
                    var pbR1 = AddBeam(pCol1TopR.Id, pInner1R.Id, MemberType.PortalBeam);
                    var pbR2 = AddBeam(pInner1R.Id, pInner2R.Id, MemberType.PortalBeam);
                    var pbR3 = AddBeam(pInner2R.Id, pCol2TopR.Id, MemberType.PortalBeam);

                    if (pbR1 != null) _startReleaseMyMzBeamIds.Add(pbR1.Id); // Released at column 1 connection
                    if (pbR3 != null) _endReleaseMyMzBeamIds.Add(pbR3.Id);   // Released at column 2 connection

                    // 2. Inclined Portal Legs / Cross Lugs (connecting strut member at 0.5m offset to column base)
                    var legR1 = AddBeam(pInner1R.Id, pBase1R.Id, MemberType.PortalKneeBrace, "PORTAL_LEG");
                    var legR2 = AddBeam(pInner2R.Id, pBase2R.Id, MemberType.PortalKneeBrace, "PORTAL_LEG");

                    if (legR1 != null) _endReleaseMyMzBeamIds.Add(legR1.Id); // Released at base
                    if (legR2 != null) _endReleaseMyMzBeamIds.Add(legR2.Id); // Released at base

                    // 3. Subdivided Upper Wall Cross-Bracing above Portal Beam
                    double upperDyRight = eaveHRight - portalHRight;
                    double diagUpperRight = Math.Sqrt(upperDyRight * upperDyRight + bayLength * bayLength);
                    int numDivUpperRight = 1;
                    if (diagUpperRight > maxBraceLength)
                    {
                        numDivUpperRight = 2;
                        while (numDivUpperRight < 10)
                        {
                            double subDy = upperDyRight / numDivUpperRight;
                            if (Math.Sqrt(subDy * subDy + bayLength * bayLength) <= maxBraceLength)
                                break;
                            numDivUpperRight++;
                        }
                    }

                    // Intermediate wall strut pipes in upper portal region
                    for (int d = 1; d < numDivUpperRight; d++)
                    {
                        double wy = Math.Round(portalHRight + d * (upperDyRight / numDivUpperRight), 4);
                        var sNodeA = AddNode(totalWidth, wy, currFrame.Z);
                        var sNodeB = AddNode(totalWidth, wy, nextFrame.Z);
                        AddBeam(sNodeA.Id, sNodeB.Id, MemberType.EaveStrut, "WALL_STRUT");
                    }

                    // Tiered Upper Wall Cross-Bracing
                    for (int d = 0; d < numDivUpperRight; d++)
                    {
                        double yBottom = Math.Round(portalHRight + d * (upperDyRight / numDivUpperRight), 4);
                        double yTop = Math.Round(portalHRight + (d + 1) * (upperDyRight / numDivUpperRight), 4);

                        var n1 = AddNode(totalWidth, yBottom, currFrame.Z);
                        var n2 = AddNode(totalWidth, yTop, currFrame.Z);
                        var n3 = AddNode(totalWidth, yBottom, nextFrame.Z);
                        var n4 = AddNode(totalWidth, yTop, nextFrame.Z);

                        AddBeam(n1.Id, n4.Id, MemberType.WallBracing);
                        AddBeam(n2.Id, n3.Id, MemberType.WallBracing);
                    }
                }

                // Mezzanine Floor Framing
                if (config.Mezzanine != null && config.Mezzanine.Enabled && config.Mezzanine.BayIndices != null && config.Mezzanine.BayIndices.Contains(b))
                {
                    double mzY = config.Mezzanine.FloorHeight;
                    double mzStartX = Math.Max(0.0, config.Mezzanine.StartX);
                    double mzEndX = Math.Min(totalWidth, config.Mezzanine.EndX);

                    var nMzCurrLeft = AddNode(mzStartX, mzY, currFrame.Z);
                    var nMzCurrRight = AddNode(mzEndX, mzY, currFrame.Z);
                    var nMzNextLeft = AddNode(mzStartX, mzY, nextFrame.Z);
                    var nMzNextRight = AddNode(mzEndX, mzY, nextFrame.Z);

                    AddBeam(nMzCurrLeft.Id, nMzCurrRight.Id, MemberType.MezzanineMainBeam);
                    AddBeam(nMzNextLeft.Id, nMzNextRight.Id, MemberType.MezzanineMainBeam);
                    AddBeam(nMzCurrLeft.Id, nMzNextLeft.Id, MemberType.MezzanineMainBeam);
                    AddBeam(nMzCurrRight.Id, nMzNextRight.Id, MemberType.MezzanineMainBeam);

                    double width = mzEndX - mzStartX;
                    int joistCount = Math.Max(1, (int)Math.Floor(width / config.Mezzanine.JoistSpacing));
                    double actualSpacing = width / joistCount;

                    for (int j = 1; j < joistCount; j++)
                    {
                        double jx = mzStartX + j * actualSpacing;
                        var jNodeA = AddNode(jx, mzY, currFrame.Z);
                        var jNodeB = AddNode(jx, mzY, nextFrame.Z);
                        AddBeam(jNodeA.Id, jNodeB.Id, MemberType.MezzanineSecondaryBeam);
                    }
                }

                // Left Wall Canopy: Longitudinal Outer Runner, Wall Strut, and Plan Cross-Bracing
                if (config.Canopy.LeftWall.IsInBay(b, config.BaySpacings.Count) && currFrame.LeftCanopyTip != null && nextFrame.LeftCanopyTip != null)
                {
                    AddBeam(currFrame.LeftCanopyTip.Id, nextFrame.LeftCanopyTip.Id, MemberType.CanopyRunner, "CANOPY_RUNNER");
                    if (currFrame.LeftCanopyNode != null && nextFrame.LeftCanopyNode != null)
                    {
                        AddBeam(currFrame.LeftCanopyNode.Id, nextFrame.LeftCanopyNode.Id, MemberType.CanopyRunner, "CANOPY_WALL_STRUT");
                    }
                    if (config.Bracing != null && config.Bracing.BracedBayIndices != null && config.Bracing.BracedBayIndices.Contains(b))
                    {
                        if (currFrame.LeftCanopyNode != null && nextFrame.LeftCanopyNode != null)
                        {
                            AddBeam(currFrame.LeftCanopyNode.Id, nextFrame.LeftCanopyTip.Id, MemberType.CanopyBracing, "CANOPY_BRACE");
                            AddBeam(currFrame.LeftCanopyTip.Id, nextFrame.LeftCanopyNode.Id, MemberType.CanopyBracing, "CANOPY_BRACE");
                        }
                    }
                }

                // Right Wall Canopy: Longitudinal Outer Runner, Wall Strut, and Plan Cross-Bracing
                if (config.Canopy.RightWall.IsInBay(b, config.BaySpacings.Count) && currFrame.RightCanopyTip != null && nextFrame.RightCanopyTip != null)
                {
                    AddBeam(currFrame.RightCanopyTip.Id, nextFrame.RightCanopyTip.Id, MemberType.CanopyRunner, "CANOPY_RUNNER");
                    if (currFrame.RightCanopyNode != null && nextFrame.RightCanopyNode != null)
                    {
                        AddBeam(currFrame.RightCanopyNode.Id, nextFrame.RightCanopyNode.Id, MemberType.CanopyRunner, "CANOPY_WALL_STRUT");
                    }
                    if (config.Bracing != null && config.Bracing.BracedBayIndices != null && config.Bracing.BracedBayIndices.Contains(b))
                    {
                        if (currFrame.RightCanopyNode != null && nextFrame.RightCanopyNode != null)
                        {
                            AddBeam(currFrame.RightCanopyNode.Id, nextFrame.RightCanopyTip.Id, MemberType.CanopyBracing, "CANOPY_BRACE");
                            AddBeam(currFrame.RightCanopyTip.Id, nextFrame.RightCanopyNode.Id, MemberType.CanopyBracing, "CANOPY_BRACE");
                        }
                    }
                }
            }

            // 3. Front Wall Canopy (Z = totalLength)
            if (!isSingleMidFrame && config.Canopy.FrontWall.Enabled)
            {
                var frontColX = frontPostOffsets.Where(x => x > 0.05 && x < totalWidth - 0.05).Distinct().OrderBy(x => x).ToList();
                frontColX.Insert(0, 0.0);
                frontColX.Add(totalWidth);
                int numFrontBays = frontColX.Count - 1;

                double fH = config.Canopy.FrontWall.Height;
                double fP = config.Canopy.FrontWall.Projection;
                double fD = config.Canopy.FrontWall.Drop;
                double fZ = totalLength;

                var tipNodes = new Dictionary<int, Node3D>();
                var startNodes = new Dictionary<int, Node3D>();

                for (int i = 0; i < frontColX.Count; i++)
                {
                    bool hasCanopy = (i > 0 && config.Canopy.FrontWall.IsInBay(i - 1, numFrontBays)) ||
                                     (i < numFrontBays && config.Canopy.FrontWall.IsInBay(i, numFrontBays));
                    if (hasCanopy)
                    {
                        double cx = frontColX[i];
                        var sNode = AddNode(cx, fH, fZ);
                        var tNode = AddNode(cx, fH - fD, fZ + fP);
                        startNodes[i] = sNode;
                        tipNodes[i] = tNode;
                        AddBeam(sNode.Id, tNode.Id, MemberType.CanopyRafter, "CANOPY_RAFTER");
                    }
                }

                for (int k = 0; k < numFrontBays; k++)
                {
                    if (config.Canopy.FrontWall.IsInBay(k, numFrontBays))
                    {
                        if (tipNodes.ContainsKey(k) && tipNodes.ContainsKey(k + 1) &&
                            startNodes.ContainsKey(k) && startNodes.ContainsKey(k + 1))
                        {
                            AddBeam(tipNodes[k].Id, tipNodes[k + 1].Id, MemberType.CanopyRunner, "CANOPY_RUNNER");
                            AddBeam(startNodes[k].Id, startNodes[k + 1].Id, MemberType.CanopyRunner, "CANOPY_WALL_STRUT");
                            AddBeam(startNodes[k].Id, tipNodes[k + 1].Id, MemberType.CanopyBracing, "CANOPY_BRACE");
                            AddBeam(tipNodes[k].Id, startNodes[k + 1].Id, MemberType.CanopyBracing, "CANOPY_BRACE");
                        }
                    }
                }
            }

            // 4. Rear Wall Canopy (Z = 0.0)
            if (!isSingleMidFrame && config.Canopy.RearWall.Enabled)
            {
                var rearColX = rearPostOffsets.Where(x => x > 0.05 && x < totalWidth - 0.05).Distinct().OrderBy(x => x).ToList();
                rearColX.Insert(0, 0.0);
                rearColX.Add(totalWidth);
                int numRearBays = rearColX.Count - 1;

                double rH = config.Canopy.RearWall.Height;
                double rP = config.Canopy.RearWall.Projection;
                double rD = config.Canopy.RearWall.Drop;
                double rZ = 0.0;

                var tipNodes = new Dictionary<int, Node3D>();
                var startNodes = new Dictionary<int, Node3D>();

                for (int i = 0; i < rearColX.Count; i++)
                {
                    bool hasCanopy = (i > 0 && config.Canopy.RearWall.IsInBay(i - 1, numRearBays)) ||
                                     (i < numRearBays && config.Canopy.RearWall.IsInBay(i, numRearBays));
                    if (hasCanopy)
                    {
                        double cx = rearColX[i];
                        var sNode = AddNode(cx, rH, rZ);
                        var tNode = AddNode(cx, rH - rD, rZ - rP);
                        startNodes[i] = sNode;
                        tipNodes[i] = tNode;
                        AddBeam(sNode.Id, tNode.Id, MemberType.CanopyRafter, "CANOPY_RAFTER");
                    }
                }

                for (int k = 0; k < numRearBays; k++)
                {
                    if (config.Canopy.RearWall.IsInBay(k, numRearBays))
                    {
                        if (tipNodes.ContainsKey(k) && tipNodes.ContainsKey(k + 1) &&
                            startNodes.ContainsKey(k) && startNodes.ContainsKey(k + 1))
                        {
                            AddBeam(tipNodes[k].Id, tipNodes[k + 1].Id, MemberType.CanopyRunner, "CANOPY_RUNNER");
                            AddBeam(startNodes[k].Id, startNodes[k + 1].Id, MemberType.CanopyRunner, "CANOPY_WALL_STRUT");
                            AddBeam(startNodes[k].Id, tipNodes[k + 1].Id, MemberType.CanopyBracing, "CANOPY_BRACE");
                            AddBeam(tipNodes[k].Id, startNodes[k + 1].Id, MemberType.CanopyBracing, "CANOPY_BRACE");
                        }
                    }
                }
            }

            var model = new GeneratedModel
            {
                Configuration = config,
                Nodes = _nodes,
                Beams = _beams,
                BaseNodeIds = _baseNodeIds,
                FixedBaseNodeIds = _fixedBaseNodeIds,
                PinnedBaseNodeIds = _pinnedBaseNodeIds,
                TensionOnlyBeamIds = _tensionBeamIds,
                PinnedEndBeamIds = _pinnedBeamIds,
                StartReleaseMyMzBeamIds = _startReleaseMyMzBeamIds,
                EndReleaseMyMzBeamIds = _endReleaseMyMzBeamIds,
                Beta90BeamIds = _beta90BeamIds
            };
            BreakMembersAtJoints(model);
            return model;
        }

        private double CalculateSpanRafterY(double x, double spanStartX, double spanEndX, double ridgeX, double eaveLeft, double eaveRight, double ridgeY)
        {
            if (x <= ridgeX)
            {
                double frac = (ridgeX - spanStartX > 0.001) ? (x - spanStartX) / (ridgeX - spanStartX) : 0.0;
                return Math.Round(eaveLeft + frac * (ridgeY - eaveLeft), 4);
            }
            else
            {
                double frac = (spanEndX - ridgeX > 0.001) ? (x - ridgeX) / (spanEndX - ridgeX) : 0.0;
                return Math.Round(ridgeY - frac * (ridgeY - eaveRight), 4);
            }
        }

        private class FrameRecord
        {
            public double Z { get; set; }
            public List<SpanRecord> Spans { get; set; } = new();
            public Node3D? RccWallNodeLeft { get; set; }
            public Node3D? RccWallNodeRight { get; set; }
            public Node3D? LeftCanopyNode { get; set; }
            public Node3D? LeftCanopyTip { get; set; }
            public Node3D? RightCanopyNode { get; set; }
            public Node3D? RightCanopyTip { get; set; }
        }

        private class SpanRecord
        {
            public Node3D BaseLeft { get; set; } = new();
            public Node3D EaveLeft { get; set; } = new();
            public Node3D Ridge { get; set; } = new();
            public Node3D EaveRight { get; set; } = new();
            public Node3D BaseRight { get; set; } = new();
            public List<Node3D> RafterNodes { get; set; } = new();
            public Node3D? CableTrayTipLeft { get; set; }
            public Node3D? CableTrayTipRight { get; set; }
        }

        private class TemplateFrameRecord
        {
            public double Z { get; set; }
            public Node3D? LeftColBase { get; set; }
            public Node3D? RightColBase { get; set; }
            public Node3D? LeftWallNode { get; set; }
            public Node3D? RightWallNode { get; set; }
            public Node3D? LeftEave { get; set; }
            public Node3D? RightEave { get; set; }
            public Node3D? Ridge { get; set; }
            public Node3D? LeftCanopyNode { get; set; }
            public Node3D? LeftCanopyTip { get; set; }
            public Node3D? RightCanopyNode { get; set; }
            public Node3D? RightCanopyTip { get; set; }
            public List<Node3D> RafterNodes { get; set; } = new();
            public Dictionary<double, Node3D> StrutNodes { get; set; } = new();
            public Dictionary<double, Node3D> JackTieNodes { get; set; } = new();
        }

        public GeneratedModel BuildFrom2DTemplate(PortalConfiguration config)
        {
            Template2DFrameDefinition? template = config.Template2DFrame;
            string customProps = string.Empty;

            if (!string.IsNullOrWhiteSpace(config.Template2DStdText))
            {
                var parsed = StaadStdParser.Parse(config.Template2DStdText);
                template = parsed.TemplateFrame;
                customProps = parsed.CustomPropertyText;
            }

            if (template == null || template.Nodes.Count == 0)
            {
                config.GenerationMode = "parametric";
                return Build(config);
            }

            _nodes.Clear();
            _beams.Clear();
            _baseNodeIds.Clear();
            _fixedBaseNodeIds.Clear();
            _pinnedBaseNodeIds.Clear();
            _tensionBeamIds.Clear();
            _pinnedBeamIds.Clear();
            _startReleaseMyMzBeamIds.Clear();
            _endReleaseMyMzBeamIds.Clear();
            _beta90BeamIds.Clear();
            _nodeCounter = 1;
            _beamCounter = 1;

            // 1. Determine Building Bounds (using base nodes to avoid cantilever/canopy distortion)
            var baseNodes2D = template.Nodes.Where(n => n.IsBase || Math.Abs(n.Y) < 0.25).ToList();
            double minX = baseNodes2D.Count > 0 ? baseNodes2D.Min(n => n.X) : template.Nodes.Min(n => n.X);
            double maxX = baseNodes2D.Count > 0 ? baseNodes2D.Max(n => n.X) : template.Nodes.Max(n => n.X);
            double spanWidth = Math.Round(maxX - minX, 4);
            double minY = template.Nodes.Min(n => n.Y);
            double maxY = template.Nodes.Max(n => n.Y);

            // 2. Auto-detect RCC Wall Height from 1st node above base on exterior columns
            double eaveEstimate = Math.Min(
                template.Nodes.Where(n => Math.Abs(n.X - minX) < 0.15).Select(n => n.Y).DefaultIfEmpty(7.0).Max(),
                template.Nodes.Where(n => Math.Abs(n.X - maxX) < 0.15).Select(n => n.Y).DefaultIfEmpty(7.0).Max()
            );

            var extWallNodes2D = template.Nodes
                .Where(n => (Math.Abs(n.X - minX) < 0.15 || Math.Abs(n.X - maxX) < 0.15) && n.Y > 0.25 && n.Y < eaveEstimate - 0.5)
                .OrderBy(n => n.Y)
                .ToList();

            double rccWallH = 0.0;
            if (extWallNodes2D.Count > 0)
            {
                rccWallH = Math.Round(extWallNodes2D.First().Y, 4);
                config.RccWallHeight = rccWallH;
            }
            else if (config.RccWallHeight > 0.25 && config.RccWallHeight < eaveEstimate - 0.5)
            {
                rccWallH = config.RccWallHeight;
            }

            // 3. Canopy Detection on Left and Right Exterior Columns
            Template2DNode? leftCanopyTip2D = template.Nodes.Where(n => n.X < minX - 0.15).OrderBy(n => n.X).FirstOrDefault();
            Template2DNode? leftCanopyColNode2D = null;
            if (leftCanopyTip2D != null)
            {
                var canopyBeam = template.Beams.FirstOrDefault(b =>
                    (template.Nodes.Any(n => n.Id == b.NodeA && n.X < minX - 0.15) && template.Nodes.Any(n => n.Id == b.NodeB && n.X >= minX - 0.15)) ||
                    (template.Nodes.Any(n => n.Id == b.NodeB && n.X < minX - 0.15) && template.Nodes.Any(n => n.Id == b.NodeA && n.X >= minX - 0.15)));

                if (canopyBeam != null)
                {
                    int colNodeId = template.Nodes.Any(n => n.Id == canopyBeam.NodeA && n.X < minX - 0.15) ? canopyBeam.NodeB : canopyBeam.NodeA;
                    leftCanopyColNode2D = template.Nodes.FirstOrDefault(n => n.Id == colNodeId);
                }
                else
                {
                    leftCanopyColNode2D = template.Nodes
                        .Where(n => Math.Abs(n.X - minX) < 0.35 && n.Y > 0.5)
                        .OrderBy(n => Math.Abs(n.Y - leftCanopyTip2D.Y))
                        .FirstOrDefault();
                }
            }

            Template2DNode? rightCanopyTip2D = template.Nodes.Where(n => n.X > maxX + 0.15).OrderByDescending(n => n.X).FirstOrDefault();
            Template2DNode? rightCanopyColNode2D = null;
            if (rightCanopyTip2D != null)
            {
                var canopyBeam = template.Beams.FirstOrDefault(b =>
                    (template.Nodes.Any(n => n.Id == b.NodeA && n.X > maxX + 0.15) && template.Nodes.Any(n => n.Id == b.NodeB && n.X <= maxX + 0.15)) ||
                    (template.Nodes.Any(n => n.Id == b.NodeB && n.X > maxX + 0.15) && template.Nodes.Any(n => n.Id == b.NodeA && n.X <= maxX + 0.15)));

                if (canopyBeam != null)
                {
                    int colNodeId = template.Nodes.Any(n => n.Id == canopyBeam.NodeA && n.X > maxX + 0.15) ? canopyBeam.NodeB : canopyBeam.NodeA;
                    rightCanopyColNode2D = template.Nodes.FirstOrDefault(n => n.Id == colNodeId);
                }
                else
                {
                    rightCanopyColNode2D = template.Nodes
                        .Where(n => Math.Abs(n.X - maxX) < 0.35 && n.Y > 0.5)
                        .OrderBy(n => Math.Abs(n.Y - rightCanopyTip2D.Y))
                        .FirstOrDefault();
                }
            }

            bool templateHasLeftCanopy = leftCanopyColNode2D != null;
            bool templateHasRightCanopy = rightCanopyColNode2D != null;

            if (templateHasLeftCanopy && !config.Canopy.Enabled)
            {
                config.Canopy.LeftWall.Enabled = true;
                if (leftCanopyColNode2D != null) config.Canopy.LeftWall.Height = leftCanopyColNode2D.Y;
            }
            if (templateHasRightCanopy && !config.Canopy.Enabled)
            {
                config.Canopy.RightWall.Enabled = true;
                if (rightCanopyColNode2D != null) config.Canopy.RightWall.Height = rightCanopyColNode2D.Y;
            }

            bool hasLeftCanopy = config.Canopy.LeftWall.Enabled;
            bool hasRightCanopy = config.Canopy.RightWall.Enabled;

            double leftCanopyY = config.Canopy.LeftWall.Height;
            double rightCanopyY = config.Canopy.RightWall.Height;

            // Update configuration span info if missing
            if (config.Spans == null || config.Spans.Count == 0)
            {
                config.Spans = new List<SpanDefinition> { new SpanDefinition(spanWidth, maxY, 10.0) };
            }

            // Extract rafter nodes
            var rafterNodeIds = template.Beams
                .Where(b => b.Type == MemberType.Rafter)
                .SelectMany(b => new[] { b.NodeA, b.NodeB })
                .Distinct()
                .ToHashSet();

            if (rafterNodeIds.Count == 0)
            {
                rafterNodeIds = template.Nodes.Where(n => n.Y > minY + 1.0 && n.X >= minX - 0.05 && n.X <= maxX + 0.05).Select(n => n.Id).ToHashSet();
            }

            var leftEave2D = template.Nodes.Where(n => Math.Abs(n.X - minX) < 0.35).OrderByDescending(n => n.Y).FirstOrDefault();
            var rightEave2D = template.Nodes.Where(n => Math.Abs(n.X - maxX) < 0.35).OrderByDescending(n => n.Y).FirstOrDefault();
            double eaveYLeft = leftEave2D?.Y ?? (minY + 6.0);
            double eaveYRight = rightEave2D?.Y ?? (minY + 6.0);

            var rafterNodes2D = template.Nodes
                .Where(n => rafterNodeIds.Contains(n.Id) && n.X >= minX - 0.05 && n.X <= maxX + 0.05)
                .Where(n => (!hasRightCanopy || Math.Abs(n.X - maxX) > 0.35 || n.Y > rightCanopyY + 0.3) &&
                            (!hasLeftCanopy || Math.Abs(n.X - minX) > 0.35 || n.Y > leftCanopyY + 0.3))
                .OrderBy(n => n.X)
                .ToList();

            if (leftEave2D == null) leftEave2D = rafterNodes2D.FirstOrDefault();
            if (rightEave2D == null) rightEave2D = rafterNodes2D.LastOrDefault();
            var ridge2D = rafterNodes2D.OrderByDescending(n => n.Y).FirstOrDefault();

            // 4. Bay Spacing along Z
            bool isZeroBay = !string.IsNullOrWhiteSpace(config.BaySpacingExpression) &&
                (config.BaySpacingExpression.Trim() == "0" || config.BaySpacingExpression.Trim() == "0.0" ||
                 (double.TryParse(config.BaySpacingExpression.Trim(), out double zVal) && zVal == 0.0 && !config.BaySpacingExpression.Contains('@')));

            if (isZeroBay)
            {
                config.BaySpacings = new List<double>();
            }
            else if (!string.IsNullOrWhiteSpace(config.BaySpacingExpression))
            {
                config.BaySpacings = SpacingParser.ParseBaySpacings(config.BaySpacingExpression);
            }
            else if (config.BaySpacings == null || config.BaySpacings.Count == 0)
            {
                config.BaySpacings = new List<double> { 6.0, 6.0, 6.0, 6.0 };
            }

            bool isSingleMidFrame = isZeroBay || config.BaySpacings.Count == 0;
            double totalLength = isSingleMidFrame ? 0.0 : config.TotalLength;
            int totalBays = isSingleMidFrame ? 0 : config.TotalBays;

            var zCoords = isSingleMidFrame ? new List<double> { 0.0 } : new List<double> { totalLength };
            if (!isSingleMidFrame)
            {
                double currentZ = totalLength;
                foreach (var s in config.BaySpacings)
                {
                    currentZ -= s;
                    zCoords.Add(Math.Round(currentZ, 4));
                }
            }

            // 5. Interior Intermediate Column Lines Detection (from 2D template nodes, beams, and widthModuleExpression)
            var interiorColXList = new List<double>();
            var intBaseNodes = template.Nodes.Where(n => (n.IsBase || Math.Abs(n.Y) < 0.25) && n.X > minX + 0.15 && n.X < maxX - 0.15).ToList();
            foreach (var bn in intBaseNodes)
            {
                double ix = Math.Round(bn.X, 4);
                if (!interiorColXList.Any(ex => Math.Abs(ex - ix) < 0.05))
                    interiorColXList.Add(ix);
            }
            foreach (var b in template.Beams.Where(b => b.Type == MemberType.IntermediateColumn))
            {
                var nA = template.Nodes.FirstOrDefault(n => n.Id == b.NodeA);
                if (nA != null && nA.X > minX + 0.15 && nA.X < maxX - 0.15)
                {
                    double ix = Math.Round(nA.X, 4);
                    if (!interiorColXList.Any(ex => Math.Abs(ex - ix) < 0.05))
                        interiorColXList.Add(ix);
                }
            }
            if (!string.IsNullOrWhiteSpace(config.WidthModuleExpression))
            {
                var offsets = SpacingParser.ParseGablePostOffsets(config.WidthModuleExpression, spanWidth);
                foreach (var ox in offsets)
                {
                    double ix = Math.Round(minX + ox, 4);
                    if (ix > minX + 0.15 && ix < maxX - 0.15 && !interiorColXList.Any(ex => Math.Abs(ex - ix) < 0.05))
                        interiorColXList.Add(ix);
                }
            }
            interiorColXList.Sort();

            // 6. Jack Portal Configuration (Intermediate columns along Z, skipped frames, Jack Beams & Stub Posts)
            bool isJackPortalEnabled = config.JackPortal != null && config.JackPortal.Enabled && interiorColXList.Count > 0;
            var jackColZList = new List<double>();
            if (isJackPortalEnabled && !string.IsNullOrWhiteSpace(config.JackPortal?.IntermediateBaySpacingExpression))
            {
                var intSpacings = SpacingParser.ParseBaySpacings(config.JackPortal.IntermediateBaySpacingExpression);
                jackColZList.Add(totalLength);
                double currentJackZ = totalLength;
                foreach (var s in intSpacings)
                {
                    currentJackZ -= s;
                    if (currentJackZ > 0.05)
                    {
                        jackColZList.Add(Math.Round(currentJackZ, 4));
                    }
                }
                if (!jackColZList.Any(zVal => Math.Abs(zVal) < 0.05))
                {
                    jackColZList.Add(0.0);
                }
            }

            bool HasJackBeamInBay(double zA, double zB, double mx)
            {
                if (!isJackPortalEnabled || !interiorColXList.Any(x => Math.Abs(x - mx) < 0.05)) return false;
                double bayMin = Math.Min(zA, zB);
                double bayMax = Math.Max(zA, zB);
                double zLeft = jackColZList.Where(jz => jz <= bayMin + 0.01).DefaultIfEmpty(0.0).Max();
                double zRight = jackColZList.Where(jz => jz >= bayMax - 0.01).DefaultIfEmpty(totalLength).Min();
                return zCoords.Any(zf => zf > zLeft + 0.05 && zf < zRight - 0.05);
            }

            bool HasJackBeamAtColumn(double z, double mx)
            {
                if (!isJackPortalEnabled || !interiorColXList.Any(x => Math.Abs(x - mx) < 0.05)) return false;
                int idx = zCoords.FindIndex(zc => Math.Abs(zc - z) < 0.05);
                if (idx < 0)
                {
                    double zPrev = zCoords.Where(zc => zc > z + 0.01).DefaultIfEmpty(totalLength).Min();
                    double zNext = zCoords.Where(zc => zc < z - 0.01).DefaultIfEmpty(0.0).Max();
                    return HasJackBeamInBay(zPrev, zNext, mx);
                }
                bool leftBay = (idx > 0) && HasJackBeamInBay(zCoords[idx - 1], zCoords[idx], mx);
                bool rightBay = (idx < zCoords.Count - 1) && HasJackBeamInBay(zCoords[idx], zCoords[idx + 1], mx);
                return leftBay || rightBay;
            }

            // 7. Cable Tray Configuration
            string cableLoc = config.CableTray?.Location?.ToLowerInvariant() ?? "side_intermediate";
            bool cableTrayEnabled = config.CableTray?.Enabled ?? false;
            bool includeSide = cableTrayEnabled;
            bool includeIntermediate = cableTrayEnabled && (cableLoc == "side_intermediate" || cableLoc == "all");
            bool includeGable = cableTrayEnabled && (cableLoc == "all");
            double cableTrayH = config.CableTray?.BracketHeight ?? 5.0;
            double cableTrayL = config.CableTray?.BracketLength ?? 0.8;

            // 8. Gable Post Offsets
            string frontGableExpr = config.GableWalls?.GableBaySpacingFront ?? "3";
            string rearGableExpr = config.GableWalls?.GableBaySpacingRear ?? "3";
            var frontOffsets = SpacingParser.ParseGablePostOffsets(frontGableExpr, spanWidth);
            var rearOffsets = SpacingParser.ParseGablePostOffsets(rearGableExpr, spanWidth);

            var gablePostXList = new List<double>();
            foreach (var ox in frontOffsets)
            {
                double gx = Math.Round(minX + ox, 4);
                if (gx > minX + 0.15 && gx < maxX - 0.15 && !gablePostXList.Any(ex => Math.Abs(ex - gx) < 0.05))
                    gablePostXList.Add(gx);
            }
            foreach (var ox in rearOffsets)
            {
                double gx = Math.Round(minX + ox, 4);
                if (gx > minX + 0.15 && gx < maxX - 0.15 && !gablePostXList.Any(ex => Math.Abs(ex - gx) < 0.05))
                    gablePostXList.Add(gx);
            }

            // 9. Longitudinal Roof Strut Lines & MaxBraceLength Panel Subdivision
            double maxBraceLength = config.Bracing?.MaxBraceLength ?? 12.5;
            if (maxBraceLength < 3.0) maxBraceLength = 12.5;
            double maxBayZ = config.BaySpacings.Count > 0 ? config.BaySpacings.Max() : 6.0;

            var distinctRafterX = new List<double> { minX, maxX };
            foreach (var rn in rafterNodes2D)
            {
                double rx = Math.Round(rn.X, 4);
                if (!distinctRafterX.Any(ex => Math.Abs(ex - rx) < 0.05))
                    distinctRafterX.Add(rx);
            }
            foreach (var gx in gablePostXList)
            {
                if (!distinctRafterX.Any(ex => Math.Abs(ex - gx) < 0.05))
                    distinctRafterX.Add(gx);
            }
            foreach (var mx in interiorColXList)
            {
                if (!distinctRafterX.Any(ex => Math.Abs(ex - mx) < 0.05))
                    distinctRafterX.Add(mx);
            }
            distinctRafterX.Sort();

            var roofStrutXList = new List<double>();
            for (int i = 0; i < distinctRafterX.Count - 1; i++)
            {
                double xA = distinctRafterX[i];
                double xB = distinctRafterX[i + 1];
                roofStrutXList.Add(xA);

                double yA = InterpolateRafterY(rafterNodes2D, xA);
                double yB = InterpolateRafterY(rafterNodes2D, xB);
                double dx = xB - xA;
                double dy = yB - yA;
                double diag = Math.Sqrt(dx * dx + dy * dy + maxBayZ * maxBayZ);

                if (diag > maxBraceLength)
                {
                    int numDiv = 2;
                    while (numDiv < 10)
                    {
                        double subDx = dx / numDiv;
                        double subDy = dy / numDiv;
                        if (Math.Sqrt(subDx * subDx + subDy * subDy + maxBayZ * maxBayZ) <= maxBraceLength)
                            break;
                        numDiv++;
                    }
                    for (int d = 1; d < numDiv; d++)
                    {
                        double interX = Math.Round(xA + d * (dx / numDiv), 4);
                        if (!roofStrutXList.Any(ex => Math.Abs(ex - interX) < 0.05))
                            roofStrutXList.Add(interX);
                    }
                }
            }
            if (!roofStrutXList.Any(ex => Math.Abs(ex - maxX) < 0.05))
                roofStrutXList.Add(maxX);
            roofStrutXList.Sort();

            var templateBeamTo3DMap = new Dictionary<int, List<int>>();
            var frameRecords = new List<TemplateFrameRecord>();

            // 7. Setup Portal Bracing Y Levels and Wall Subdivision Lookups for 2D to 3D converter
            double portalHLeft = 4.5;
            double portalHRight = 4.5;
            double legOffset = (config.Bracing != null && config.Bracing.PortalLegOffset > 0.01) ? config.Bracing.PortalLegOffset : 0.5;
            if (config.Bracing != null)
            {
                portalHLeft = Math.Min(config.Bracing.PortalBracingHeight, eaveYLeft - 0.3);
                if (portalHLeft < 1.0) portalHLeft = eaveYLeft * 0.65;
                portalHLeft = Math.Round(portalHLeft, 4);

                portalHRight = Math.Min(config.Bracing.PortalBracingHeight, eaveYRight - 0.3);
                if (portalHRight < 1.0) portalHRight = eaveYRight * 0.65;
                portalHRight = Math.Round(portalHRight, 4);
            }

            var frameLeftWallYLookup = new List<HashSet<double>>();
            var frameRightWallYLookup = new List<HashSet<double>>();
            for (int i = 0; i < zCoords.Count; i++)
            {
                frameLeftWallYLookup.Add(new HashSet<double>());
                frameRightWallYLookup.Add(new HashSet<double>());
            }

            if (config.Bracing != null && config.Bracing.EnablePortalBracing)
            {
                for (int b = 0; b < zCoords.Count - 1; b++)
                {
                    double bayZ = Math.Abs(zCoords[b] - zCoords[b + 1]);

                    // Left Wall Portal Bracing intermediate Y levels
                    if (config.Bracing.IsPortalBayLeft(b))
                    {
                        if (!frameLeftWallYLookup[b].Contains(portalHLeft)) frameLeftWallYLookup[b].Add(portalHLeft);
                        if (!frameLeftWallYLookup[b + 1].Contains(portalHLeft)) frameLeftWallYLookup[b + 1].Add(portalHLeft);

                        double upperDyLeft = eaveYLeft - portalHLeft;
                        double diagUpperLeft = Math.Sqrt(upperDyLeft * upperDyLeft + bayZ * bayZ);
                        int numDivUpperLeft = 1;
                        if (diagUpperLeft > maxBraceLength)
                        {
                            numDivUpperLeft = 2;
                            while (numDivUpperLeft < 10)
                            {
                                double subDy = upperDyLeft / numDivUpperLeft;
                                if (Math.Sqrt(subDy * subDy + bayZ * bayZ) <= maxBraceLength) break;
                                numDivUpperLeft++;
                            }
                        }
                        for (int d = 1; d < numDivUpperLeft; d++)
                        {
                            double wy = Math.Round(portalHLeft + d * (upperDyLeft / numDivUpperLeft), 4);
                            if (!frameLeftWallYLookup[b].Contains(wy)) frameLeftWallYLookup[b].Add(wy);
                            if (!frameLeftWallYLookup[b + 1].Contains(wy)) frameLeftWallYLookup[b + 1].Add(wy);
                        }
                    }

                    // Right Wall Portal Bracing intermediate Y levels
                    if (config.Bracing.IsPortalBayRight(b))
                    {
                        if (!frameRightWallYLookup[b].Contains(portalHRight)) frameRightWallYLookup[b].Add(portalHRight);
                        if (!frameRightWallYLookup[b + 1].Contains(portalHRight)) frameRightWallYLookup[b + 1].Add(portalHRight);

                        double upperDyRight = eaveYRight - portalHRight;
                        double diagUpperRight = Math.Sqrt(upperDyRight * upperDyRight + bayZ * bayZ);
                        int numDivUpperRight = 1;
                        if (diagUpperRight > maxBraceLength)
                        {
                            numDivUpperRight = 2;
                            while (numDivUpperRight < 10)
                            {
                                double subDy = upperDyRight / numDivUpperRight;
                                if (Math.Sqrt(subDy * subDy + bayZ * bayZ) <= maxBraceLength) break;
                                numDivUpperRight++;
                            }
                        }
                        for (int d = 1; d < numDivUpperRight; d++)
                        {
                            double wy = Math.Round(portalHRight + d * (upperDyRight / numDivUpperRight), 4);
                            if (!frameRightWallYLookup[b].Contains(wy)) frameRightWallYLookup[b].Add(wy);
                            if (!frameRightWallYLookup[b + 1].Contains(wy)) frameRightWallYLookup[b + 1].Add(wy);
                        }
                    }
                }
            }

            // 8. Generate Extruded Frames along Z
            for (int f = 0; f < zCoords.Count; f++)
            {
                double z = zCoords[f];
                bool isGableEndFrame = (!isSingleMidFrame) && (f == 0 || f == zCoords.Count - 1);
                var node2DTo3D = new Dictionary<int, Node3D>();

                foreach (var tNode in template.Nodes)
                {
                    // If Gable End Frame or Skipped Interior Frame: Omit base nodes of 2D interior intermediate columns
                    bool isInteriorBaseNode = (tNode.IsBase || Math.Abs(tNode.Y) < 0.25) &&
                                              tNode.X > minX + 0.15 && tNode.X < maxX - 0.15;
                    bool isSkippedInteriorFrame = isJackPortalEnabled && !jackColZList.Any(jz => Math.Abs(jz - z) < 0.05);
                    if ((isGableEndFrame || isSkippedInteriorFrame) && isInteriorBaseNode)
                    {
                        continue;
                    }

                    var n3D = AddNode(tNode.X, tNode.Y, z);
                    node2DTo3D[tNode.Id] = n3D;

                    if (tNode.IsBase || Math.Abs(tNode.Y) < 0.25)
                    {
                        if (tNode.SupportType.Equals("FIXED", StringComparison.OrdinalIgnoreCase) ||
                            (string.IsNullOrEmpty(tNode.SupportType) && (Math.Abs(tNode.X - minX) < 0.15 || Math.Abs(tNode.X - maxX) < 0.15)))
                        {
                            AddFixedBaseNode(n3D);
                        }
                        else
                        {
                            AddPinnedBaseNode(n3D);
                        }
                    }
                }

                bool addedLeftCableBracket = false;
                bool addedRightCableBracket = false;

                bool hasLeftOnF = hasLeftCanopy && (isSingleMidFrame || (f > 0 && config.Canopy.LeftWall.IsInBay(f - 1, totalBays)) || (f < totalBays && config.Canopy.LeftWall.IsInBay(f, totalBays)));
                bool hasRightOnF = hasRightCanopy && (isSingleMidFrame || (f > 0 && config.Canopy.RightWall.IsInBay(f - 1, totalBays)) || (f < totalBays && config.Canopy.RightWall.IsInBay(f, totalBays)));

                foreach (var tBeam in template.Beams)
                {
                    if (!node2DTo3D.TryGetValue(tBeam.NodeA, out var nA3D) || !node2DTo3D.TryGetValue(tBeam.NodeB, out var nB3D))
                        continue;

                    // Omit 2D interior intermediate columns (handled explicitly below for both Jack Portal and normal modes)
                    bool isStrictlyInterior = nA3D.X > minX + 0.15 && nA3D.X < maxX - 0.15;
                    bool isInteriorCol = isStrictlyInterior && (
                        tBeam.Type == MemberType.IntermediateColumn ||
                        (Math.Abs(nA3D.X - nB3D.X) < 0.15 && Math.Min(nA3D.Y, nB3D.Y) < 0.5));
                    if (isInteriorCol)
                    {
                        continue;
                    }

                    // Check if tBeam is a Left or Right Canopy beam in 2D template
                    bool isLeftCanopyBeam = (nA3D.X < minX - 0.15 || nB3D.X < minX - 0.15);
                    bool isRightCanopyBeam = (nA3D.X > maxX + 0.15 || nB3D.X > maxX + 0.15);

                    if (isLeftCanopyBeam && !hasLeftOnF) continue;
                    if (isRightCanopyBeam && !hasRightOnF) continue;

                    // Column Intermediate Splitting (Cable Tray & Portal Bracing Heights):
                    bool isLeftCol = Math.Abs(nA3D.X - minX) < 0.15 && Math.Abs(nB3D.X - minX) < 0.15 &&
                                     (tBeam.Type == MemberType.Column || Math.Min(nA3D.Y, nB3D.Y) < 0.5);
                    bool isRightCol = Math.Abs(nA3D.X - maxX) < 0.15 && Math.Abs(nB3D.X - maxX) < 0.15 &&
                                      (tBeam.Type == MemberType.Column || Math.Min(nA3D.Y, nB3D.Y) < 0.5);

                    if (isLeftCol || isRightCol)
                    {
                        double colX = isLeftCol ? minX : maxX;
                        double eaveH = isLeftCol ? eaveYLeft : eaveYRight;
                        double yMin = Math.Min(nA3D.Y, nB3D.Y);
                        double yMax = Math.Max(nA3D.Y, nB3D.Y);

                        var splitYList = new List<double>();
                        if (includeSide && cableTrayH < eaveH - 0.3 && cableTrayH > yMin + 0.05 && cableTrayH < yMax - 0.05)
                        {
                            splitYList.Add(cableTrayH);
                        }

                        if (isLeftCol && hasLeftOnF && leftCanopyY > yMin + 0.05 && leftCanopyY < yMax - 0.05 && !splitYList.Any(sy => Math.Abs(sy - leftCanopyY) < 0.05))
                        {
                            splitYList.Add(leftCanopyY);
                        }
                        if (isRightCol && hasRightOnF && rightCanopyY > yMin + 0.05 && rightCanopyY < yMax - 0.05 && !splitYList.Any(sy => Math.Abs(sy - rightCanopyY) < 0.05))
                        {
                            splitYList.Add(rightCanopyY);
                        }

                        if (isGableEndFrame && config.Canopy.FrontWall.Enabled && f == 0)
                        {
                            var frontColX = frontOffsets.Select(ox => Math.Round(minX + ox, 4)).Where(x => x > minX + 0.05 && x < maxX - 0.05).Distinct().OrderBy(x => x).ToList();
                            frontColX.Insert(0, minX);
                            frontColX.Add(maxX);
                            int numFrontBays = frontColX.Count - 1;
                            double fH = config.Canopy.FrontWall.Height;
                            if (isLeftCol && config.Canopy.FrontWall.IsInBay(0, numFrontBays) && fH > yMin + 0.05 && fH < yMax - 0.05 && !splitYList.Any(sy => Math.Abs(sy - fH) < 0.05))
                                splitYList.Add(fH);
                            if (isRightCol && config.Canopy.FrontWall.IsInBay(numFrontBays - 1, numFrontBays) && fH > yMin + 0.05 && fH < yMax - 0.05 && !splitYList.Any(sy => Math.Abs(sy - fH) < 0.05))
                                splitYList.Add(fH);
                        }
                        if (isGableEndFrame && config.Canopy.RearWall.Enabled && f == zCoords.Count - 1)
                        {
                            var rearColX = rearOffsets.Select(ox => Math.Round(minX + ox, 4)).Where(x => x > minX + 0.05 && x < maxX - 0.05).Distinct().OrderBy(x => x).ToList();
                            rearColX.Insert(0, minX);
                            rearColX.Add(maxX);
                            int numRearBays = rearColX.Count - 1;
                            double rH = config.Canopy.RearWall.Height;
                            if (isLeftCol && config.Canopy.RearWall.IsInBay(0, numRearBays) && rH > yMin + 0.05 && rH < yMax - 0.05 && !splitYList.Any(sy => Math.Abs(sy - rH) < 0.05))
                                splitYList.Add(rH);
                            if (isRightCol && config.Canopy.RearWall.IsInBay(numRearBays - 1, numRearBays) && rH > yMin + 0.05 && rH < yMax - 0.05 && !splitYList.Any(sy => Math.Abs(sy - rH) < 0.05))
                                splitYList.Add(rH);
                        }

                        var wallYSet = isLeftCol ? frameLeftWallYLookup[f] : frameRightWallYLookup[f];
                        foreach (var wy in wallYSet)
                        {
                            if (wy > yMin + 0.05 && wy < yMax - 0.05 && !splitYList.Any(sy => Math.Abs(sy - wy) < 0.05))
                            {
                                splitYList.Add(wy);
                            }
                        }

                        if (splitYList.Count > 0)
                        {
                            splitYList.Sort();
                            var nBot = nA3D.Y < nB3D.Y ? nA3D : nB3D;
                            var nTop = nA3D.Y < nB3D.Y ? nB3D : nA3D;

                            var colNodes = new List<Node3D> { nBot };
                            foreach (var sy in splitYList)
                            {
                                var sn = AddNode(colX, sy, z);
                                colNodes.Add(sn);

                                if (includeSide && Math.Abs(sy - cableTrayH) < 0.05)
                                {
                                    double tipX = isLeftCol ? (minX + cableTrayL) : (maxX - cableTrayL);
                                    var tipCable = AddNode(tipX, cableTrayH, z);
                                    AddBeam(sn.Id, tipCable.Id, MemberType.CableTrayBracket);
                                    if (isLeftCol) addedLeftCableBracket = true;
                                    else addedRightCableBracket = true;
                                }
                            }
                            colNodes.Add(nTop);

                            if (!templateBeamTo3DMap.ContainsKey(tBeam.Id)) templateBeamTo3DMap[tBeam.Id] = new List<int>();

                            double totalH = Math.Abs(nTop.Y - nBot.Y);
                            for (int ci = 0; ci < colNodes.Count - 1; ci++)
                            {
                                double tStart = totalH > 0.001 ? Math.Max(0.0, Math.Min(1.0, (colNodes[ci].Y - nBot.Y) / totalH)) : 0.0;
                                double tEnd = totalH > 0.001 ? Math.Max(0.0, Math.Min(1.0, (colNodes[ci + 1].Y - nBot.Y) / totalH)) : 1.0;
                                if (nA3D.Y > nB3D.Y)
                                {
                                    tStart = 1.0 - tStart;
                                    tEnd = 1.0 - tEnd;
                                }
                                string colSegProp = TaperedSectionHelper.InterpolateTaperedProperty(tBeam.SectionProperty, tStart, tEnd);

                                var cb = AddBeam(colNodes[ci].Id, colNodes[ci + 1].Id, tBeam.Type, tBeam.GroupName, colSegProp);
                                if (cb != null)
                                {
                                    if (ci == 0 && tBeam.StartReleaseMyMz) _startReleaseMyMzBeamIds.Add(cb.Id);
                                    if (ci == colNodes.Count - 2 && tBeam.EndReleaseMyMz) _endReleaseMyMzBeamIds.Add(cb.Id);
                                    if (tBeam.Beta90) _beta90BeamIds.Add(cb.Id);
                                    templateBeamTo3DMap[tBeam.Id].Add(cb.Id);
                                }
                            }
                            continue;
                        }
                    }

                    var beam3D = AddBeam(nA3D.Id, nB3D.Id, tBeam.Type, tBeam.GroupName, tBeam.SectionProperty);
                    if (beam3D != null)
                    {
                        if (tBeam.StartReleaseMyMz) _startReleaseMyMzBeamIds.Add(beam3D.Id);
                        if (tBeam.EndReleaseMyMz) _endReleaseMyMzBeamIds.Add(beam3D.Id);
                        if (tBeam.Beta90) _beta90BeamIds.Add(beam3D.Id);

                        if (!templateBeamTo3DMap.ContainsKey(tBeam.Id))
                            templateBeamTo3DMap[tBeam.Id] = new List<int>();
                        templateBeamTo3DMap[tBeam.Id].Add(beam3D.Id);
                    }
                }

                // If template had NO canopy in 2D but Canopy is enabled in UI:
                Node3D? genLeftCanopyCol = null;
                Node3D? genLeftCanopyTip = null;
                if (config.Canopy.LeftWall.Enabled && leftCanopyTip2D == null && hasLeftOnF && config.Canopy.LeftWall.Height < eaveYLeft - 0.3)
                {
                    genLeftCanopyCol = AddNode(minX, config.Canopy.LeftWall.Height, z);
                    genLeftCanopyTip = AddNode(minX - config.Canopy.LeftWall.Projection, config.Canopy.LeftWall.Height - config.Canopy.LeftWall.Drop, z);
                    AddBeam(genLeftCanopyCol.Id, genLeftCanopyTip.Id, MemberType.CanopyRafter, "CANOPY_RAFTER");
                }

                Node3D? genRightCanopyCol = null;
                Node3D? genRightCanopyTip = null;
                if (config.Canopy.RightWall.Enabled && rightCanopyTip2D == null && hasRightOnF && config.Canopy.RightWall.Height < eaveYRight - 0.3)
                {
                    genRightCanopyCol = AddNode(maxX, config.Canopy.RightWall.Height, z);
                    genRightCanopyTip = AddNode(maxX + config.Canopy.RightWall.Projection, config.Canopy.RightWall.Height - config.Canopy.RightWall.Drop, z);
                    AddBeam(genRightCanopyCol.Id, genRightCanopyTip.Id, MemberType.CanopyRafter, "CANOPY_RAFTER");
                }

                // If column had a pre-existing node at cableTrayH or no split happened, ensure bracket is added
                if (includeSide && !addedLeftCableBracket && cableTrayH < eaveYLeft - 0.3)
                {
                    var colCable = AddNode(minX, cableTrayH, z);
                    var tipCable = AddNode(minX + cableTrayL, cableTrayH, z);
                    AddBeam(colCable.Id, tipCable.Id, MemberType.CableTrayBracket);
                }
                if (includeSide && !addedRightCableBracket && cableTrayH < eaveYRight - 0.3)
                {
                    var colCable = AddNode(maxX, cableTrayH, z);
                    var tipCable = AddNode(maxX - cableTrayL, cableTrayH, z);
                    AddBeam(colCable.Id, tipCable.Id, MemberType.CableTrayBracket);
                }

                var fRecord = new TemplateFrameRecord
                {
                    Z = z,
                    LeftColBase = node2DTo3D.Values.Where(n => Math.Abs(n.X - minX) < 0.35).OrderBy(n => n.Y).FirstOrDefault(),
                    RightColBase = node2DTo3D.Values.Where(n => Math.Abs(n.X - maxX) < 0.35).OrderBy(n => n.Y).FirstOrDefault(),
                    LeftWallNode = node2DTo3D.Values.FirstOrDefault(n => Math.Abs(n.X - minX) < 0.35 && Math.Abs(n.Y - rccWallH) < 0.25),
                    RightWallNode = node2DTo3D.Values.FirstOrDefault(n => Math.Abs(n.X - maxX) < 0.35 && Math.Abs(n.Y - rccWallH) < 0.25),
                    LeftEave = (leftEave2D != null && node2DTo3D.ContainsKey(leftEave2D.Id))
                        ? node2DTo3D[leftEave2D.Id]
                        : node2DTo3D.Values.Where(n => Math.Abs(n.X - minX) < 0.35).OrderByDescending(n => n.Y).FirstOrDefault()
                        ?? AddNode(minX, eaveYLeft, z),
                    RightEave = (rightEave2D != null && node2DTo3D.ContainsKey(rightEave2D.Id))
                        ? node2DTo3D[rightEave2D.Id]
                        : node2DTo3D.Values.Where(n => Math.Abs(n.X - maxX) < 0.35).OrderByDescending(n => n.Y).FirstOrDefault()
                        ?? AddNode(maxX, eaveYRight, z),
                    Ridge = ridge2D != null && node2DTo3D.ContainsKey(ridge2D.Id) ? node2DTo3D[ridge2D.Id] : null,
                    LeftCanopyNode = (leftCanopyColNode2D != null && node2DTo3D.ContainsKey(leftCanopyColNode2D.Id)) ? node2DTo3D[leftCanopyColNode2D.Id] : genLeftCanopyCol,
                    LeftCanopyTip = (leftCanopyTip2D != null && node2DTo3D.ContainsKey(leftCanopyTip2D.Id)) ? node2DTo3D[leftCanopyTip2D.Id] : genLeftCanopyTip,
                    RightCanopyNode = (rightCanopyColNode2D != null && node2DTo3D.ContainsKey(rightCanopyColNode2D.Id)) ? node2DTo3D[rightCanopyColNode2D.Id] : genRightCanopyCol,
                    RightCanopyTip = (rightCanopyTip2D != null && node2DTo3D.ContainsKey(rightCanopyTip2D.Id)) ? node2DTo3D[rightCanopyTip2D.Id] : genRightCanopyTip,
                    RafterNodes = rafterNodes2D.Where(rn => node2DTo3D.ContainsKey(rn.Id)).Select(rn => node2DTo3D[rn.Id]).ToList()
                };

                // Populate / create strut nodes on this frame at all roofStrutXList positions
                foreach (var sx in roofStrutXList)
                {
                    double sy = InterpolateRafterY(rafterNodes2D, sx);
                    var existingNode = node2DTo3D.Values.FirstOrDefault(n => Math.Abs(n.X - sx) < 0.05 && Math.Abs(n.Y - sy) < 0.1);
                    if (existingNode != null)
                    {
                        fRecord.StrutNodes[sx] = existingNode;
                    }
                    else
                    {
                        var strutNode = AddNode(sx, sy, z);
                        fRecord.StrutNodes[sx] = strutNode;
                    }
                }

                // Front & Rear Gable Wind Posts (attached directly to rafter strut nodes)
                if (isGableEndFrame)
                {
                    var offsets = (f == 0) ? frontOffsets : rearOffsets;
                    foreach (var ox in offsets)
                    {
                        double gx = Math.Round(minX + ox, 4);
                        if (gx <= minX + 0.15 || gx >= maxX - 0.15) continue;

                        var baseNode = AddNode(gx, 0.0, z);
                        AddPinnedBaseNode(baseNode);

                        Node3D? topNode = null;
                        var matchedX = fRecord.StrutNodes.Keys.FirstOrDefault(k => Math.Abs(k - gx) < 0.05);
                        if (fRecord.StrutNodes.ContainsKey(matchedX))
                        {
                            topNode = fRecord.StrutNodes[matchedX];
                        }
                        else
                        {
                            double topY = InterpolateRafterY(rafterNodes2D, gx);
                            topNode = AddNode(gx, topY, z);
                            fRecord.StrutNodes[gx] = topNode;
                        }

                        var keyGableNodes = new List<Node3D> { baseNode, topNode };

                        if (includeGable && cableTrayH < topNode.Y - 0.3)
                        {
                            var colGableCable = AddNode(gx, cableTrayH, z);
                            var tipGableCable = AddNode(gx + cableTrayL, cableTrayH, z);
                            AddBeam(colGableCable.Id, tipGableCable.Id, MemberType.CableTrayBracket);
                            keyGableNodes.Add(colGableCable);
                        }

                        // ONLY break column if a Jack Beam is actually connected to it!
                        if (isJackPortalEnabled && !isSingleMidFrame && HasJackBeamAtColumn(z, gx))
                        {
                            double topY = topNode.Y;
                            double tieY = Math.Round(Math.Max(0.5, topY - 1.0), 4);
                            var tieNode = AddNode(gx, tieY, z);
                            fRecord.JackTieNodes[gx] = tieNode;
                            keyGableNodes.Add(tieNode);
                        }

                        // Break gable post at Canopy height if Front/Rear canopy is active on this post
                        if (f == 0 && config.Canopy.FrontWall.Enabled && config.Canopy.FrontWall.Height < topNode.Y - 0.2)
                        {
                            var frontColX = frontOffsets.Select(o => Math.Round(minX + o, 4)).Where(x => x > minX + 0.05 && x < maxX - 0.05).Distinct().OrderBy(x => x).ToList();
                            frontColX.Insert(0, minX);
                            frontColX.Add(maxX);
                            int numFrontBays = frontColX.Count - 1;
                            int pIdx = frontColX.FindIndex(x => Math.Abs(x - gx) < 0.05);
                            bool hasCanopy = (pIdx > 0 && config.Canopy.FrontWall.IsInBay(pIdx - 1, numFrontBays)) ||
                                             (pIdx >= 0 && pIdx < numFrontBays && config.Canopy.FrontWall.IsInBay(pIdx, numFrontBays));
                            if (hasCanopy)
                            {
                                var canNode = AddNode(gx, config.Canopy.FrontWall.Height, z);
                                keyGableNodes.Add(canNode);
                            }
                        }
                        if (f == zCoords.Count - 1 && config.Canopy.RearWall.Enabled && config.Canopy.RearWall.Height < topNode.Y - 0.2)
                        {
                            var rearColX = rearOffsets.Select(o => Math.Round(minX + o, 4)).Where(x => x > minX + 0.05 && x < maxX - 0.05).Distinct().OrderBy(x => x).ToList();
                            rearColX.Insert(0, minX);
                            rearColX.Add(maxX);
                            int numRearBays = rearColX.Count - 1;
                            int pIdx = rearColX.FindIndex(x => Math.Abs(x - gx) < 0.05);
                            bool hasCanopy = (pIdx > 0 && config.Canopy.RearWall.IsInBay(pIdx - 1, numRearBays)) ||
                                             (pIdx >= 0 && pIdx < numRearBays && config.Canopy.RearWall.IsInBay(pIdx, numRearBays));
                            if (hasCanopy)
                            {
                                var canNode = AddNode(gx, config.Canopy.RearWall.Height, z);
                                keyGableNodes.Add(canNode);
                            }
                        }

                        keyGableNodes = keyGableNodes.OrderBy(n => n.Y).Distinct().ToList();
                        for (int i = 0; i < keyGableNodes.Count - 1; i++)
                        {
                            var gb = AddBeam(keyGableNodes[i].Id, keyGableNodes[i + 1].Id, MemberType.GablePost, "GABLE_POST");
                            if (gb != null)
                            {
                                _beta90BeamIds.Add(gb.Id);
                                if (i == keyGableNodes.Count - 2)
                                {
                                    _endReleaseMyMzBeamIds.Add(gb.Id);
                                }
                            }
                        }
                    }
                }
                else if (interiorColXList.Count > 0)
                {
                    // Intermediate Columns on Interior Frames (including Jack Portal logic)
                    foreach (var mx in interiorColXList)
                    {
                        double topY = InterpolateRafterY(rafterNodes2D, mx);
                        var topNode = fRecord.StrutNodes.ContainsKey(mx) ? fRecord.StrutNodes[mx] : AddNode(mx, topY, z);
                        double tieY = Math.Round(Math.Max(0.5, topY - 1.0), 4);

                        bool hasColumnHere = isSingleMidFrame || !isJackPortalEnabled || jackColZList.Any(jz => Math.Abs(jz - z) < 0.05);

                        if (!hasColumnHere)
                        {
                            // Skipped intermediate frame: vertical post connecting Jack Beam (1m below rafter) up to Rafter
                            var tieNode = AddNode(mx, tieY, z);
                            fRecord.JackTieNodes[mx] = tieNode;
                            var stubBeam = AddBeam(tieNode.Id, topNode.Id, MemberType.IntermediateColumn, "JACK_POST");
                            if (stubBeam != null)
                            {
                                _beta90BeamIds.Add(stubBeam.Id);
                                _startReleaseMyMzBeamIds.Add(stubBeam.Id);
                                _endReleaseMyMzBeamIds.Add(stubBeam.Id);
                            }
                        }
                        else
                        {
                            // Column present: full column from base to rafter
                            var baseMod = AddNode(mx, 0.0, z);
                            AddPinnedBaseNode(baseMod);
                            var modKeyNodes = new List<Node3D> { baseMod, topNode };

                            // ONLY break column if a Jack Beam is actually connected to it!
                            bool hasJackConnecting = !isSingleMidFrame && HasJackBeamAtColumn(z, mx);
                            if (hasJackConnecting)
                            {
                                var tieNode = AddNode(mx, tieY, z);
                                fRecord.JackTieNodes[mx] = tieNode;
                                modKeyNodes.Add(tieNode);
                            }

                            if (includeIntermediate && cableTrayH < topY - 0.3)
                            {
                                var colCable = AddNode(mx, cableTrayH, z);
                                var tipCable = AddNode(mx + cableTrayL, cableTrayH, z);
                                AddBeam(colCable.Id, tipCable.Id, MemberType.CableTrayBracket);
                                modKeyNodes.Add(colCable);
                            }

                            var origColBeam = template.Beams.FirstOrDefault(b => {
                                var na = template.Nodes.FirstOrDefault(n => n.Id == b.NodeA);
                                var nb = template.Nodes.FirstOrDefault(n => n.Id == b.NodeB);
                                return na != null && nb != null && Math.Abs(na.X - mx) < 0.15 && Math.Abs(nb.X - mx) < 0.15 &&
                                       (b.Type == MemberType.IntermediateColumn || Math.Min(na.Y, nb.Y) < 0.5);
                            });
                            string colGroup = origColBeam?.GroupName ?? "INT_COL";
                            string colSec = origColBeam?.SectionProperty ?? "";

                            modKeyNodes = modKeyNodes.OrderBy(n => n.Y).Distinct().ToList();
                            for (int i = 0; i < modKeyNodes.Count - 1; i++)
                            {
                                var colBeam = AddBeam(modKeyNodes[i].Id, modKeyNodes[i + 1].Id, MemberType.IntermediateColumn, colGroup, colSec);
                                if (colBeam != null && hasJackConnecting)
                                {
                                    _beta90BeamIds.Add(colBeam.Id);
                                    if (i == modKeyNodes.Count - 2)
                                    {
                                        _endReleaseMyMzBeamIds.Add(colBeam.Id);
                                    }
                                }
                            }
                        }
                    }
                }

                frameRecords.Add(fRecord);
            }

            // 10. 3D Longitudinal Struts, Jack Beams, Bracings, and Tie Members
            if (!isSingleMidFrame && frameRecords.Count > 1)
            {
                for (int b = 0; b < config.BaySpacings.Count; b++)
                {
                    var fCurr = frameRecords[b];
                    var fNext = frameRecords[b + 1];
                    double bayZ = config.BaySpacings[b];

                    // A. Longitudinal Struts along all roofStrutXList positions (including gable post tops and intermediate column lines!)
                    foreach (var sx in roofStrutXList)
                    {
                        if (fCurr.StrutNodes.TryGetValue(sx, out var nA) && fNext.StrutNodes.TryGetValue(sx, out var nB))
                        {
                            MemberType strutType;
                            if (Math.Abs(sx - minX) < 0.05 || Math.Abs(sx - maxX) < 0.05)
                                strutType = MemberType.EaveStrut;
                            else if (fCurr.Ridge != null && Math.Abs(sx - fCurr.Ridge.X) < 0.05)
                                strutType = MemberType.RidgeStrut;
                            else
                                strutType = MemberType.EaveStrut; // Strut pipe / tie pipe

                            AddBeam(nA.Id, nB.Id, strutType, "STRUT");
                        }
                    }

                    // B. Jack Beams along Intermediate Column Lines (1m below rafter level)
                    if (isJackPortalEnabled)
                    {
                        foreach (var mx in interiorColXList)
                        {
                            if (HasJackBeamInBay(fCurr.Z, fNext.Z, mx))
                            {
                                double topYA = InterpolateRafterY(rafterNodes2D, mx);
                                double topYB = InterpolateRafterY(rafterNodes2D, mx);
                                double tieYA = Math.Round(Math.Max(0.5, topYA - 1.0), 4);
                                double tieYB = Math.Round(Math.Max(0.5, topYB - 1.0), 4);
                                var tieNodeA = fCurr.JackTieNodes.TryGetValue(mx, out var tA) ? tA : AddNode(mx, tieYA, fCurr.Z);
                                var tieNodeB = fNext.JackTieNodes.TryGetValue(mx, out var tB) ? tB : AddNode(mx, tieYB, fNext.Z);
                                AddBeam(tieNodeA.Id, tieNodeB.Id, MemberType.JackBeam);
                            }
                        }
                    }

                    // B. Braced Bays: Roof and Wall X-Bracing (No member along wall height as per engineering requirement)
                    bool isBracedBay = config.Bracing != null &&
                        ((config.Bracing.BracedBayIndices != null && config.Bracing.BracedBayIndices.Contains(b)) ||
                         (config.Bracing.BracedBayIndices == null || config.Bracing.BracedBayIndices.Count == 0 ? (b == 0 || b == config.BaySpacings.Count - 1) : false));

                    bool isPortalBayLeft = config.Bracing?.IsPortalBayLeft(b) ?? false;
                    bool isPortalBayRight = config.Bracing?.IsPortalBayRight(b) ?? false;
                    double curLegOffset = Math.Min(legOffset, bayZ * 0.35);

                    if (config.Bracing != null)
                    {
                        // 1. Roof X-Bracing placed between strut lines (Requirement 3 & 5)
                        if (isBracedBay && config.Bracing.IncludeRoofBracing)
                        {
                            for (int i = 0; i < roofStrutXList.Count - 1; i++)
                            {
                                double x1 = roofStrutXList[i];
                                double x2 = roofStrutXList[i + 1];

                                if (fCurr.StrutNodes.TryGetValue(x1, out var p1) &&
                                    fCurr.StrutNodes.TryGetValue(x2, out var p2) &&
                                    fNext.StrutNodes.TryGetValue(x1, out var p3) &&
                                    fNext.StrutNodes.TryGetValue(x2, out var p4))
                                {
                                    AddBeam(p1.Id, p4.Id, MemberType.RoofBracing, "ROOF_BRACE");
                                    AddBeam(p2.Id, p3.Id, MemberType.RoofBracing, "ROOF_BRACE");
                                }
                            }
                        }

                        // 2. Left Wall Bracing: Portal Bracing OR Wall X-Bracing
                        if (isPortalBayLeft)
                        {
                            var pCol1Top = AddNode(minX, portalHLeft, fCurr.Z);
                            var pCol2Top = AddNode(minX, portalHLeft, fNext.Z);
                            var pInner1 = AddNode(minX, portalHLeft, fCurr.Z - curLegOffset);
                            var pInner2 = AddNode(minX, portalHLeft, fNext.Z + curLegOffset);
                            var pBase1 = fCurr.LeftColBase ?? AddNode(minX, 0.0, fCurr.Z);
                            var pBase2 = fNext.LeftColBase ?? AddNode(minX, 0.0, fNext.Z);

                            // Horizontal Portal Header Beam (3 segments)
                            var pb1 = AddBeam(pCol1Top.Id, pInner1.Id, MemberType.PortalBeam, "PORTAL_BEAM");
                            var pb2 = AddBeam(pInner1.Id, pInner2.Id, MemberType.PortalBeam, "PORTAL_BEAM");
                            var pb3 = AddBeam(pInner2.Id, pCol2Top.Id, MemberType.PortalBeam, "PORTAL_BEAM");
                            if (pb1 != null) _startReleaseMyMzBeamIds.Add(pb1.Id);
                            if (pb3 != null) _endReleaseMyMzBeamIds.Add(pb3.Id);

                            // Inclined Portal Legs (released at base)
                            var leg1 = AddBeam(pInner1.Id, pBase1.Id, MemberType.PortalKneeBrace, "PORTAL_LEG");
                            var leg2 = AddBeam(pInner2.Id, pBase2.Id, MemberType.PortalKneeBrace, "PORTAL_LEG");
                            if (leg1 != null) _endReleaseMyMzBeamIds.Add(leg1.Id);
                            if (leg2 != null) _endReleaseMyMzBeamIds.Add(leg2.Id);

                            // Subdivided Upper Wall Cross-Bracing above Portal Beam
                            var nEaveA = fCurr.LeftEave ?? AddNode(minX, eaveYLeft, fCurr.Z);
                            var nEaveB = fNext.LeftEave ?? AddNode(minX, eaveYLeft, fNext.Z);
                            AddWallXBracingPanel(pCol1Top, nEaveA, pCol2Top, nEaveB, minX, bayZ, maxBraceLength);
                        }
                        else if (isBracedBay && config.Bracing.IncludeWallBracing)
                        {
                            bool isLeftCanopyBay = config.Canopy.LeftWall.IsInBay(b, totalBays);

                            // Left Wall
                            if (isLeftCanopyBay && fCurr.LeftCanopyNode != null && fNext.LeftCanopyNode != null)
                            {
                                AddBeam(fCurr.LeftCanopyNode.Id, fNext.LeftCanopyNode.Id, MemberType.CanopyRunner, "CANOPY_WALL_STRUT");

                                var nBaseA = fCurr.LeftColBase ?? AddNode(minX, 0.0, fCurr.Z);
                                var nBaseB = fNext.LeftColBase ?? AddNode(minX, 0.0, fNext.Z);
                                AddWallXBracingPanel(nBaseA, fCurr.LeftCanopyNode, nBaseB, fNext.LeftCanopyNode, minX, bayZ, maxBraceLength);

                                var nEaveA = fCurr.LeftEave ?? AddNode(minX, eaveYLeft, fCurr.Z);
                                var nEaveB = fNext.LeftEave ?? AddNode(minX, eaveYLeft, fNext.Z);
                                if (nEaveA.Y > fCurr.LeftCanopyNode.Y + 0.3)
                                {
                                    AddWallXBracingPanel(fCurr.LeftCanopyNode, nEaveA, fNext.LeftCanopyNode, nEaveB, minX, bayZ, maxBraceLength);
                                }
                            }
                            else
                            {
                                var nBaseA = fCurr.LeftColBase ?? AddNode(minX, 0.0, fCurr.Z);
                                var nBaseB = fNext.LeftColBase ?? AddNode(minX, 0.0, fNext.Z);
                                var nEaveA = fCurr.LeftEave ?? AddNode(minX, eaveYLeft, fCurr.Z);
                                var nEaveB = fNext.LeftEave ?? AddNode(minX, eaveYLeft, fNext.Z);
                                AddWallXBracingPanel(nBaseA, nEaveA, nBaseB, nEaveB, minX, bayZ, maxBraceLength);
                            }
                        }

                        // 3. Right Wall Bracing: Portal Bracing OR Wall X-Bracing
                        if (isPortalBayRight)
                        {
                            var pCol1TopR = AddNode(maxX, portalHRight, fCurr.Z);
                            var pCol2TopR = AddNode(maxX, portalHRight, fNext.Z);
                            var pInner1R = AddNode(maxX, portalHRight, fCurr.Z - curLegOffset);
                            var pInner2R = AddNode(maxX, portalHRight, fNext.Z + curLegOffset);
                            var pBase1R = fCurr.RightColBase ?? AddNode(maxX, 0.0, fCurr.Z);
                            var pBase2R = fNext.RightColBase ?? AddNode(maxX, 0.0, fNext.Z);

                            // Horizontal Portal Header Beam (3 segments)
                            var pbR1 = AddBeam(pCol1TopR.Id, pInner1R.Id, MemberType.PortalBeam, "PORTAL_BEAM");
                            var pbR2 = AddBeam(pInner1R.Id, pInner2R.Id, MemberType.PortalBeam, "PORTAL_BEAM");
                            var pbR3 = AddBeam(pInner2R.Id, pCol2TopR.Id, MemberType.PortalBeam, "PORTAL_BEAM");
                            if (pbR1 != null) _startReleaseMyMzBeamIds.Add(pbR1.Id);
                            if (pbR3 != null) _endReleaseMyMzBeamIds.Add(pbR3.Id);

                            // Inclined Portal Legs (released at base)
                            var legR1 = AddBeam(pInner1R.Id, pBase1R.Id, MemberType.PortalKneeBrace, "PORTAL_LEG");
                            var legR2 = AddBeam(pInner2R.Id, pBase2R.Id, MemberType.PortalKneeBrace, "PORTAL_LEG");
                            if (legR1 != null) _endReleaseMyMzBeamIds.Add(legR1.Id);
                            if (legR2 != null) _endReleaseMyMzBeamIds.Add(legR2.Id);

                            // Subdivided Upper Wall Cross-Bracing above Portal Beam
                            var nEaveAR = fCurr.RightEave ?? AddNode(maxX, eaveYRight, fCurr.Z);
                            var nEaveBR = fNext.RightEave ?? AddNode(maxX, eaveYRight, fNext.Z);
                            AddWallXBracingPanel(pCol1TopR, nEaveAR, pCol2TopR, nEaveBR, maxX, bayZ, maxBraceLength);
                        }
                        else if (isBracedBay && config.Bracing.IncludeWallBracing)
                        {
                            bool isRightCanopyBay = config.Canopy.RightWall.IsInBay(b, totalBays);

                            // Right Wall
                            if (isRightCanopyBay && fCurr.RightCanopyNode != null && fNext.RightCanopyNode != null)
                            {
                                AddBeam(fCurr.RightCanopyNode.Id, fNext.RightCanopyNode.Id, MemberType.CanopyRunner, "CANOPY_WALL_STRUT");

                                var nBaseA = fCurr.RightColBase ?? AddNode(maxX, 0.0, fCurr.Z);
                                var nBaseB = fNext.RightColBase ?? AddNode(maxX, 0.0, fNext.Z);
                                AddWallXBracingPanel(nBaseA, fCurr.RightCanopyNode, nBaseB, fNext.RightCanopyNode, maxX, bayZ, maxBraceLength);

                                var nEaveA = fCurr.RightEave ?? AddNode(maxX, eaveYRight, fCurr.Z);
                                var nEaveB = fNext.RightEave ?? AddNode(maxX, eaveYRight, fNext.Z);
                                if (nEaveA.Y > fCurr.RightCanopyNode.Y + 0.3)
                                {
                                    AddWallXBracingPanel(fCurr.RightCanopyNode, nEaveA, fNext.RightCanopyNode, nEaveB, maxX, bayZ, maxBraceLength);
                                }
                            }
                            else
                            {
                                var nBaseA = fCurr.RightColBase ?? AddNode(maxX, 0.0, fCurr.Z);
                                var nBaseB = fNext.RightColBase ?? AddNode(maxX, 0.0, fNext.Z);
                                var nEaveA = fCurr.RightEave ?? AddNode(maxX, eaveYRight, fCurr.Z);
                                var nEaveB = fNext.RightEave ?? AddNode(maxX, eaveYRight, fNext.Z);
                                AddWallXBracingPanel(nBaseA, nEaveA, nBaseB, nEaveB, maxX, bayZ, maxBraceLength);
                            }
                        }
                    }

                    // D. Canopy Longitudinal Runners (active bays) and Canopy Plan Cross-Bracing
                    bool leftCanBay = config.Canopy.LeftWall.IsInBay(b, totalBays);
                    bool rightCanBay = config.Canopy.RightWall.IsInBay(b, totalBays);

                    if (leftCanBay && fCurr.LeftCanopyTip != null && fNext.LeftCanopyTip != null)
                    {
                        AddBeam(fCurr.LeftCanopyTip.Id, fNext.LeftCanopyTip.Id, MemberType.CanopyRunner, "CANOPY_RUNNER");
                        if (fCurr.LeftCanopyNode != null && fNext.LeftCanopyNode != null)
                        {
                            AddBeam(fCurr.LeftCanopyNode.Id, fNext.LeftCanopyNode.Id, MemberType.CanopyRunner, "CANOPY_WALL_STRUT");
                        }
                        if (isBracedBay && fCurr.LeftCanopyNode != null && fNext.LeftCanopyNode != null)
                        {
                            AddBeam(fCurr.LeftCanopyNode.Id, fNext.LeftCanopyTip.Id, MemberType.CanopyBracing, "CANOPY_BRACE");
                            AddBeam(fCurr.LeftCanopyTip.Id, fNext.LeftCanopyNode.Id, MemberType.CanopyBracing, "CANOPY_BRACE");
                        }
                    }

                    if (rightCanBay && fCurr.RightCanopyTip != null && fNext.RightCanopyTip != null)
                    {
                        AddBeam(fCurr.RightCanopyTip.Id, fNext.RightCanopyTip.Id, MemberType.CanopyRunner, "CANOPY_RUNNER");
                        if (fCurr.RightCanopyNode != null && fNext.RightCanopyNode != null)
                        {
                            AddBeam(fCurr.RightCanopyNode.Id, fNext.RightCanopyNode.Id, MemberType.CanopyRunner, "CANOPY_WALL_STRUT");
                        }
                        if (isBracedBay && fCurr.RightCanopyNode != null && fNext.RightCanopyNode != null)
                        {
                            AddBeam(fCurr.RightCanopyNode.Id, fNext.RightCanopyTip.Id, MemberType.CanopyBracing, "CANOPY_BRACE");
                            AddBeam(fCurr.RightCanopyTip.Id, fNext.RightCanopyNode.Id, MemberType.CanopyBracing, "CANOPY_BRACE");
                        }
                    }
                }
            }

            // Front Wall Canopy (Z = totalLength)
            if (!isSingleMidFrame && config.Canopy.FrontWall.Enabled)
            {
                var frontColX = frontOffsets.Select(o => Math.Round(minX + o, 4)).Where(x => x > minX + 0.05 && x < maxX - 0.05).Distinct().OrderBy(x => x).ToList();
                frontColX.Insert(0, minX);
                frontColX.Add(maxX);
                int numFrontBays = frontColX.Count - 1;

                double fH = config.Canopy.FrontWall.Height;
                double fP = config.Canopy.FrontWall.Projection;
                double fD = config.Canopy.FrontWall.Drop;
                double fZ = totalLength;

                var tipNodes = new Dictionary<int, Node3D>();
                var startNodes = new Dictionary<int, Node3D>();

                for (int i = 0; i < frontColX.Count; i++)
                {
                    bool hasCanopy = (i > 0 && config.Canopy.FrontWall.IsInBay(i - 1, numFrontBays)) ||
                                     (i < numFrontBays && config.Canopy.FrontWall.IsInBay(i, numFrontBays));
                    if (hasCanopy)
                    {
                        double cx = frontColX[i];
                        var sNode = AddNode(cx, fH, fZ);
                        var tNode = AddNode(cx, fH - fD, fZ + fP);
                        startNodes[i] = sNode;
                        tipNodes[i] = tNode;
                        AddBeam(sNode.Id, tNode.Id, MemberType.CanopyRafter, "CANOPY_RAFTER");
                    }
                }

                for (int k = 0; k < numFrontBays; k++)
                {
                    if (config.Canopy.FrontWall.IsInBay(k, numFrontBays))
                    {
                        if (tipNodes.ContainsKey(k) && tipNodes.ContainsKey(k + 1) &&
                            startNodes.ContainsKey(k) && startNodes.ContainsKey(k + 1))
                        {
                            AddBeam(tipNodes[k].Id, tipNodes[k + 1].Id, MemberType.CanopyRunner, "CANOPY_RUNNER");
                            AddBeam(startNodes[k].Id, startNodes[k + 1].Id, MemberType.CanopyRunner, "CANOPY_WALL_STRUT");
                            AddBeam(startNodes[k].Id, tipNodes[k + 1].Id, MemberType.CanopyBracing, "CANOPY_BRACE");
                            AddBeam(tipNodes[k].Id, startNodes[k + 1].Id, MemberType.CanopyBracing, "CANOPY_BRACE");
                        }
                    }
                }
            }

            // Rear Wall Canopy (Z = 0.0)
            if (!isSingleMidFrame && config.Canopy.RearWall.Enabled)
            {
                var rearColX = rearOffsets.Select(o => Math.Round(minX + o, 4)).Where(x => x > minX + 0.05 && x < maxX - 0.05).Distinct().OrderBy(x => x).ToList();
                rearColX.Insert(0, minX);
                rearColX.Add(maxX);
                int numRearBays = rearColX.Count - 1;

                double rH = config.Canopy.RearWall.Height;
                double rP = config.Canopy.RearWall.Projection;
                double rD = config.Canopy.RearWall.Drop;
                double rZ = 0.0;

                var tipNodes = new Dictionary<int, Node3D>();
                var startNodes = new Dictionary<int, Node3D>();

                for (int i = 0; i < rearColX.Count; i++)
                {
                    bool hasCanopy = (i > 0 && config.Canopy.RearWall.IsInBay(i - 1, numRearBays)) ||
                                     (i < numRearBays && config.Canopy.RearWall.IsInBay(i, numRearBays));
                    if (hasCanopy)
                    {
                        double cx = rearColX[i];
                        var sNode = AddNode(cx, rH, rZ);
                        var tNode = AddNode(cx, rH - rD, rZ - rP);
                        startNodes[i] = sNode;
                        tipNodes[i] = tNode;
                        AddBeam(sNode.Id, tNode.Id, MemberType.CanopyRafter, "CANOPY_RAFTER");
                    }
                }

                for (int k = 0; k < numRearBays; k++)
                {
                    if (config.Canopy.RearWall.IsInBay(k, numRearBays))
                    {
                        if (tipNodes.ContainsKey(k) && tipNodes.ContainsKey(k + 1) &&
                            startNodes.ContainsKey(k) && startNodes.ContainsKey(k + 1))
                        {
                            AddBeam(tipNodes[k].Id, tipNodes[k + 1].Id, MemberType.CanopyRunner, "CANOPY_RUNNER");
                            AddBeam(startNodes[k].Id, startNodes[k + 1].Id, MemberType.CanopyRunner, "CANOPY_WALL_STRUT");
                            AddBeam(startNodes[k].Id, tipNodes[k + 1].Id, MemberType.CanopyBracing, "CANOPY_BRACE");
                            AddBeam(tipNodes[k].Id, startNodes[k + 1].Id, MemberType.CanopyBracing, "CANOPY_BRACE");
                        }
                    }
                }
            }

            var model = new GeneratedModel
            {
                Configuration = config,
                Nodes = _nodes,
                Beams = _beams,
                BaseNodeIds = _baseNodeIds,
                FixedBaseNodeIds = _fixedBaseNodeIds,
                PinnedBaseNodeIds = _pinnedBaseNodeIds,
                TensionOnlyBeamIds = _tensionBeamIds,
                PinnedEndBeamIds = _pinnedBeamIds,
                StartReleaseMyMzBeamIds = _startReleaseMyMzBeamIds,
                EndReleaseMyMzBeamIds = _endReleaseMyMzBeamIds,
                Beta90BeamIds = _beta90BeamIds,
                TemplateFrame = template,
                TemplateBeamTo3DBeamMap = templateBeamTo3DMap,
                CustomPropertyText = customProps
            };
            BreakMembersAtJoints(model);
            return model;
        }

        private double InterpolateRafterY(List<Template2DNode> rafterNodes, double x)
        {
            var sorted = rafterNodes.OrderBy(n => n.X).ToList();
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                var nA = sorted[i];
                var nB = sorted[i + 1];
                if (x >= Math.Min(nA.X, nB.X) - 0.01 && x <= Math.Max(nA.X, nB.X) + 0.01)
                {
                    double dx = nB.X - nA.X;
                    if (Math.Abs(dx) < 0.001) return Math.Max(nA.Y, nB.Y);
                    double t = (x - nA.X) / dx;
                    return Math.Round(nA.Y + t * (nB.Y - nA.Y), 4);
                }
            }
            return sorted.Count > 0 ? sorted.OrderByDescending(n => n.Y).First().Y : 7.0;
        }

        private double InterpolateRafterY(List<Node3D> rafterNodes, double x)
        {
            for (int i = 0; i < rafterNodes.Count - 1; i++)
            {
                var nA = rafterNodes[i];
                var nB = rafterNodes[i + 1];
                if (x >= Math.Min(nA.X, nB.X) - 0.01 && x <= Math.Max(nA.X, nB.X) + 0.01)
                {
                    double dx = nB.X - nA.X;
                    if (Math.Abs(dx) < 0.001) return Math.Max(nA.Y, nB.Y);
                    double t = (x - nA.X) / dx;
                    return Math.Round(nA.Y + t * (nB.Y - nA.Y), 4);
                }
            }
            return rafterNodes.Count > 0 ? rafterNodes.OrderByDescending(n => n.Y).First().Y : 7.0;
        }

        private void AddWallXBracingPanel(Node3D nBotA, Node3D nTopA, Node3D nBotB, Node3D nTopB, double x, double bayZ, double maxBraceLength)
        {
            double h = Math.Abs(nTopA.Y - nBotA.Y);
            if (h < 0.5) return;

            double diag = Math.Sqrt(h * h + bayZ * bayZ);
            int numTiers = 1;
            if (diag > maxBraceLength)
            {
                numTiers = 2;
                while (numTiers < 10)
                {
                    double subH = h / numTiers;
                    if (Math.Sqrt(subH * subH + bayZ * bayZ) <= maxBraceLength) break;
                    numTiers++;
                }
            }

            // Intermediate wall struts in tiered wall bracing
            for (int t = 1; t < numTiers; t++)
            {
                double ty = Math.Round(nBotA.Y + t * (h / numTiers), 4);
                var wsA = AddNode(x, ty, nBotA.Z);
                var wsB = AddNode(x, ty, nBotB.Z);
                AddBeam(wsA.Id, wsB.Id, MemberType.EaveStrut, "WALL_STRUT");
            }

            // Cross bracing in each tier
            for (int t = 0; t < numTiers; t++)
            {
                double ty1 = Math.Round(nBotA.Y + t * (h / numTiers), 4);
                double ty2 = Math.Round(nBotA.Y + (t + 1) * (h / numTiers), 4);

                var p1 = (t == 0) ? nBotA : AddNode(x, ty1, nBotA.Z);
                var p2 = (t == numTiers - 1) ? nTopA : AddNode(x, ty2, nBotA.Z);
                var p3 = (t == 0) ? nBotB : AddNode(x, ty1, nBotB.Z);
                var p4 = (t == numTiers - 1) ? nTopB : AddNode(x, ty2, nBotB.Z);

                AddBeam(p1.Id, p4.Id, MemberType.WallBracing, "WALL_BRACE");
                AddBeam(p2.Id, p3.Id, MemberType.WallBracing, "WALL_BRACE");
            }
        }

        public void BreakMembersAtJoints(GeneratedModel model)
        {
            if (model == null || model.Nodes == null || model.Beams == null) return;

            var allNodes = model.Nodes.Values.ToList();
            var beamsToCheck = model.Beams.Values.ToList();

            int nextBeamId = model.Beams.Keys.DefaultIfEmpty(0).Max() + 1;

            foreach (var beam in beamsToCheck)
            {
                // Skip diagonal tension/compression cross bracing from being segmented
                if (beam.Type == MemberType.RoofBracing || beam.Type == MemberType.WallBracing || beam.Type == MemberType.CanopyBracing)
                    continue;

                if (!model.Nodes.TryGetValue(beam.NodeA, out var nA) || !model.Nodes.TryGetValue(beam.NodeB, out var nB))
                    continue;

                double vx = nB.X - nA.X;
                double vy = nB.Y - nA.Y;
                double vz = nB.Z - nA.Z;
                double lenSq = vx * vx + vy * vy + vz * vz;
                if (lenSq < 1e-6) continue;

                double minX = Math.Min(nA.X, nB.X) - 0.01;
                double maxX = Math.Max(nA.X, nB.X) + 0.01;
                double minY = Math.Min(nA.Y, nB.Y) - 0.01;
                double maxY = Math.Max(nA.Y, nB.Y) + 0.01;
                double minZ = Math.Min(nA.Z, nB.Z) - 0.01;
                double maxZ = Math.Max(nA.Z, nB.Z) + 0.01;

                var intermediateJoints = new List<(Node3D Node, double T)>();

                foreach (var node in allNodes)
                {
                    if (node.Id == nA.Id || node.Id == nB.Id) continue;

                    // Bounding box filter
                    if (node.X < minX || node.X > maxX ||
                        node.Y < minY || node.Y > maxY ||
                        node.Z < minZ || node.Z > maxZ)
                    {
                        continue;
                    }

                    double wx = node.X - nA.X;
                    double wy = node.Y - nA.Y;
                    double wz = node.Z - nA.Z;

                    double t = (wx * vx + wy * vy + wz * vz) / lenSq;
                    if (t <= 0.0005 || t >= 0.9995) continue;

                    double px = wx - t * vx;
                    double py = wy - t * vy;
                    double pz = wz - t * vz;
                    double distSq = px * px + py * py + pz * pz;

                    // 8mm perpendicular tolerance
                    if (distSq <= 6.4e-5)
                    {
                        intermediateJoints.Add((node, t));
                    }
                }

                if (intermediateJoints.Count == 0) continue;

                intermediateJoints.Sort((a, b) => a.T.CompareTo(b.T));

                var uniqueJoints = new List<(Node3D Node, double T)>();
                foreach (var ij in intermediateJoints)
                {
                    if (uniqueJoints.Count == 0 || Math.Abs(ij.T - uniqueJoints.Last().T) > 0.0005)
                    {
                        uniqueJoints.Add(ij);
                    }
                }

                if (uniqueJoints.Count == 0) continue;

                var segNodes = new List<Node3D> { nA };
                var segT = new List<double> { 0.0 };
                foreach (var uj in uniqueJoints)
                {
                    segNodes.Add(uj.Node);
                    segT.Add(uj.T);
                }
                segNodes.Add(nB);
                segT.Add(1.0);

                int originalBeamId = beam.Id;
                bool hadStartRelease = model.StartReleaseMyMzBeamIds.Contains(originalBeamId);
                bool hadEndRelease = model.EndReleaseMyMzBeamIds.Contains(originalBeamId);
                bool hadBeta90 = model.Beta90BeamIds.Contains(originalBeamId);
                bool hadTensionOnly = model.TensionOnlyBeamIds.Contains(originalBeamId);
                bool hadPinnedEnd = model.PinnedEndBeamIds.Contains(originalBeamId);

                var newBeamIds = new List<int>();

                for (int i = 0; i < segNodes.Count - 1; i++)
                {
                    int beamId = (i == 0) ? originalBeamId : nextBeamId++;
                    newBeamIds.Add(beamId);

                    double tStart = segT[i];
                    double tEnd = segT[i + 1];

                    string subProp = TaperedSectionHelper.InterpolateTaperedProperty(beam.SectionProperty, tStart, tEnd);

                    var newBeam = new Beam3D
                    {
                        Id = beamId,
                        NodeA = segNodes[i].Id,
                        NodeB = segNodes[i + 1].Id,
                        Type = beam.Type,
                        GroupName = beam.GroupName,
                        SectionProperty = subProp
                    };

                    model.Beams[beamId] = newBeam;
                }

                if (hadBeta90)
                {
                    foreach (var id in newBeamIds)
                    {
                        if (!model.Beta90BeamIds.Contains(id)) model.Beta90BeamIds.Add(id);
                    }
                }

                if (hadTensionOnly)
                {
                    foreach (var id in newBeamIds)
                    {
                        if (!model.TensionOnlyBeamIds.Contains(id)) model.TensionOnlyBeamIds.Add(id);
                    }
                }

                if (hadEndRelease)
                {
                    if (newBeamIds.Count > 1)
                    {
                        model.EndReleaseMyMzBeamIds.Remove(originalBeamId);
                        int lastId = newBeamIds.Last();
                        if (!model.EndReleaseMyMzBeamIds.Contains(lastId)) model.EndReleaseMyMzBeamIds.Add(lastId);
                    }
                }

                if (hadPinnedEnd)
                {
                    if (newBeamIds.Count > 1)
                    {
                        model.PinnedEndBeamIds.Remove(originalBeamId);
                        int lastId = newBeamIds.Last();
                        if (!model.PinnedEndBeamIds.Contains(lastId)) model.PinnedEndBeamIds.Add(lastId);
                    }
                }

                if (model.TemplateBeamTo3DBeamMap != null)
                {
                    foreach (var kvp in model.TemplateBeamTo3DBeamMap)
                    {
                        if (kvp.Value.Contains(originalBeamId))
                        {
                            foreach (var id in newBeamIds)
                            {
                                if (!kvp.Value.Contains(id)) kvp.Value.Add(id);
                            }
                        }
                    }
                }
            }

            _beamCounter = Math.Max(_beamCounter, nextBeamId + 1);
        }
    }
}
