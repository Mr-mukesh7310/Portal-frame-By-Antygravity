using System;
using System.Collections.Generic;
using System.Linq;
using StaadPortalEngine.Helpers;
using StaadPortalEngine.Models;

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

        private Beam3D? AddBeam(int nodeA, int nodeB, MemberType type, string groupName = "")
        {
            if (nodeA == nodeB) return null;

            var existing = _beams.Values.FirstOrDefault(b =>
                (b.NodeA == nodeA && b.NodeB == nodeB) ||
                (b.NodeA == nodeB && b.NodeB == nodeA));

            if (existing != null)
                return existing;

            var beam = new Beam3D(_beamCounter++, nodeA, nodeB, type, groupName);
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

            // Parse bay spacing expression along Z (e.g. "5@6+5@4")
            if (!string.IsNullOrWhiteSpace(config.BaySpacingExpression))
            {
                config.BaySpacings = SpacingParser.ParseBaySpacings(config.BaySpacingExpression);
            }
            else if (config.BaySpacings == null || config.BaySpacings.Count == 0)
            {
                config.BaySpacings = new List<double> { 6.0, 6.0, 6.0, 6.0 };
            }

            double totalWidth = config.TotalWidth;
            double totalLength = config.TotalLength;

            // Frame 0 (Front Gable) at Z = TotalLength, stepping down to Z = 0 (Rear Gable)
            List<double> zCoords = new() { totalLength };
            double currentZ = totalLength;
            foreach (var spacing in config.BaySpacings)
            {
                currentZ -= spacing;
                zCoords.Add(Math.Round(currentZ, 4));
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

                var distinctX = new List<double>();
                foreach (var x in xList.OrderBy(v => v))
                {
                    if (!distinctX.Any(ex => Math.Abs(ex - x) < 0.05))
                    {
                        distinctX.Add(Math.Round(x, 4));
                    }
                }
                distinctX.Sort();

                // Enforce Max 12.5m Roof Cross Brace Length: Subdivide any panel where diagonal > 12.5m by inserting intermediate full-length strut lines
                var subdividedX = new List<double>();
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
                    bool isPortalBay = config.Bracing.EnablePortalBracing && config.Bracing.PortalBracedBayIndices != null && config.Bracing.PortalBracedBayIndices.Contains(b);
                    if (isPortalBay || !config.Bracing.BracedBayIndices.Contains(b)) continue;

                    double bayZ = config.BaySpacings[b];

                    // Left wall
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

                    // Right wall
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

            // Add Portal Bracing intermediate Y levels (Portal beam height and upper subdivided tier levels)
            if (config.Bracing != null && config.Bracing.EnablePortalBracing && config.Bracing.PortalBracedBayIndices != null)
            {
                for (int b = 0; b < config.BaySpacings.Count; b++)
                {
                    if (!config.Bracing.PortalBracedBayIndices.Contains(b)) continue;

                    double bayZ = config.BaySpacings[b];

                    // Left Wall
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

                    // Right Wall
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

            // 1. Build Transverse Portal Frames at each Z coordinate
            for (int f = 0; f < zCoords.Count; f++)
            {
                double z = zCoords[f];
                var fRecord = new FrameRecord { Z = z };

                bool isGableFront = (f == 0);
                bool isGableBack = (f == zCoords.Count - 1);
                bool isInteriorFrame = (!isGableFront && !isGableBack);

                double accumulatedX = 0.0;
                var currentGableOffsets = isGableFront ? frontPostOffsets : (isGableBack ? rearPostOffsets : new List<double>());

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

                    // BREAK LEFT COLUMN AT EACH NODE LOCATION (Base -> Wall Node -> Cable Tray Node -> Wall Bracing Strut Node -> Eave)
                    if (isExteriorLeft || isInteriorFrame)
                    {
                        var leftKeyNodes = new List<Node3D> { colBaseLeft, colEaveLeft };
                        if (rccNodeLeft != null) leftKeyNodes.Add(rccNodeLeft);
                        if (cableTrayColLeft != null) leftKeyNodes.Add(cableTrayColLeft);

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

                    // BREAK RIGHT COLUMN AT EACH NODE LOCATION (Base -> Wall Node -> Cable Tray Node -> Wall Bracing Strut Node -> Eave)
                    if (isExteriorRight)
                    {
                        var rightKeyNodes = new List<Node3D> { colBaseRight, colEaveRight };
                        if (rccNodeRight != null) rightKeyNodes.Add(rccNodeRight);
                        if (cableTrayColRight != null) rightKeyNodes.Add(cableTrayColRight);

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

                            bool hasColumnHere = !isJackPortalEnabled || jackColZList.Any(jz => Math.Abs(jz - z) < 0.05);

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

                                bool hasJackConnecting = HasJackBeamAtColumn(z, mx);
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

                bool isPortalBay = config.Bracing != null && config.Bracing.EnablePortalBracing && config.Bracing.PortalBracedBayIndices != null && config.Bracing.PortalBracedBayIndices.Contains(b);

                // Wall X-Bracing (Only for non-portal braced bays, Subdivided if diagonal > maxBraceLength with intermediate wall strut in braced bay ONLY)
                if (!isPortalBay && config.Bracing != null && config.Bracing.IncludeWallBracing && config.Bracing.BracedBayIndices != null && config.Bracing.BracedBayIndices.Contains(b))
                {
                    double bayZ = config.BaySpacings[b];

                    // --- Left Wall Bracing (X = 0) ---
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

                    // --- Right Wall Bracing (X = totalWidth) ---
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

                // Portal Bracing (Header Beam with inner 0.5m offset nodes, Tapered Legs to Base, and Subdivided Upper X-Bracing above)
                if (isPortalBay)
                {
                    double bayZ = config.BaySpacings[b];
                    double legOffset = config.Bracing!.PortalLegOffset > 0.01 ? config.Bracing.PortalLegOffset : 0.5;
                    legOffset = Math.Min(legOffset, bayZ * 0.35);

                    // --- Left Wall Portal Bracing (X = 0) ---
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

                    // --- Right Wall Portal Bracing (X = totalWidth) ---
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
            }

            return new GeneratedModel
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
    }
}
