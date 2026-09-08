using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StaadPortalEngine.Exporters;
using StaadPortalEngine.Generators;
using StaadPortalEngine.Helpers;
using StaadPortalEngine.Models;
using Xunit;

namespace StaadPortalEngine.Tests
{
    public class PortalGeneratorTests
    {
        [Fact]
        public void Test_BaySpacingMultiplierSyntax()
        {
            var bays = SpacingParser.ParseBaySpacings("5@6+5@4");
            Assert.Equal(10, bays.Count);
            Assert.Equal(50.0, bays.Sum());
            Assert.Equal(6.0, bays[0]);
            Assert.Equal(6.0, bays[4]);
            Assert.Equal(4.0, bays[5]);
            Assert.Equal(4.0, bays[9]);
        }

        [Fact]
        public void Test_GablePostSpacingSyntax_1at5Plus2at4()
        {
            var offsets = SpacingParser.ParseGablePostOffsets("1@5+2@4+1@5", 24.0);
            Assert.Equal(4, offsets.Count);
            Assert.Equal(5.0, offsets[0]);
            Assert.Equal(9.0, offsets[1]);
            Assert.Equal(13.0, offsets[2]);
            Assert.Equal(18.0, offsets[3]);
        }

        [Fact]
        public void Test_SingleGable_WithWidthModules_NoWallNodeOnIntermediateColumns()
        {
            var builder = new PortalGeometryBuilder();

            var config = new PortalConfiguration
            {
                ModelName = "Single Gable 24m with 2@12 Width Modules",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 10.0) },
                WidthModuleExpression = "2@12",
                BaySpacingExpression = "5@6+5@4", // 10 bays, 11 frames
                RccWallHeight = 2.2,
                GableWalls = new GableWallConfiguration { GableBaySpacingFront = "1@5+2@4+1@5", GableBaySpacingRear = "4@6" },
                CableTray = new CableTrayConfiguration { Enabled = true, Location = "side_intermediate", BracketHeight = 5.0, BracketLength = 0.8 },
                Bracing = new BracingConfiguration { IncludeRoofBracing = true, IncludeWallBracing = true, BracedBayIndices = new List<int> { 0, 4, 9 } }
            };

            var model = builder.Build(config);

            Assert.NotNull(model);
            Assert.Equal(24.0, config.TotalWidth);
            Assert.Equal(50.0, config.TotalLength);
            Assert.Equal(10, config.TotalBays);
            Assert.Equal(11, config.TotalFrames);

            // 1. Verify NO intermediate columns on Front Gable (Z = 50m) and Rear Gable (Z = 0m)
            var frontIntermediateCols = model.Beams.Values
                .Where(b => b.Type == MemberType.IntermediateColumn && Math.Abs(model.Nodes[b.NodeA].Z - 50.0) < 0.001)
                .ToList();
            Assert.Empty(frontIntermediateCols);

            var rearIntermediateCols = model.Beams.Values
                .Where(b => b.Type == MemberType.IntermediateColumn && Math.Abs(model.Nodes[b.NodeA].Z) < 0.001)
                .ToList();
            Assert.Empty(rearIntermediateCols);

            // 2. Verify Exterior Columns and Gable Posts DO have wall nodes at Y = 2.2m
            var exteriorWallNodes = model.Nodes.Values
                .Where(n => Math.Abs(n.Y - 2.2) < 0.001 && (Math.Abs(n.X) < 0.001 || Math.Abs(n.X - 24.0) < 0.001))
                .ToList();
            Assert.NotEmpty(exteriorWallNodes);

            var gableWallNodes = model.Nodes.Values
                .Where(n => Math.Abs(n.Y - 2.2) < 0.001 && (Math.Abs(n.Z) < 0.001 || Math.Abs(n.Z - 50.0) < 0.001) && n.X > 0.1 && n.X < 23.9)
                .ToList();
            Assert.NotEmpty(gableWallNodes);

            // 3. Verify Cable Tray Projection beams are NOT connected by any runner member
            var cableTrayRunners = model.Beams.Values
                .Where(b => b.Type == MemberType.CableTrayRunner)
                .ToList();
            Assert.Empty(cableTrayRunners);

            // Save sample file to dedicated Sample_Models folder for user inspection
            string outputDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\Sample_Models"));
            Directory.CreateDirectory(outputDir);
            string outputPath = Path.Combine(outputDir, "Sample_3D_Industrial_Portal_Model.std");
            StaadStdWriter.WriteToFile(outputPath, model);

            // Verify STAAD Export
            string std = StaadStdWriter.GenerateStdText(model);
            Assert.Contains("STAAD SPACE", std);
            Assert.Contains("JOINT COORDINATES", std);
            Assert.Contains("MEMBER INCIDENCES", std);
            Assert.Contains("SUPPORTS", std);
            Assert.Contains("FINISH", std);
        }

        [Fact]
        public void Test_RaftersAndColumns_BrokenAtEachNodeLocation()
        {
            var builder = new PortalGeometryBuilder();

            // 24m warehouse with Wall = 2.2m, Cable Tray = 5.0m, Eave = 7.0m, Gable posts at X = 6, 12, 18
            var config = new PortalConfiguration
            {
                ModelName = "Broken Members Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 10.0) },
                BaySpacingExpression = "2@6",
                RccWallHeight = 2.2,
                GableWalls = new GableWallConfiguration { GableBaySpacingFront = "4@6", GableBaySpacingRear = "4@6" },
                CableTray = new CableTrayConfiguration { Enabled = true, Location = "all", BracketHeight = 5.0, BracketLength = 0.8 },
                Bracing = new BracingConfiguration { IncludeRoofBracing = true, IncludeWallBracing = true, BracedBayIndices = new List<int> { 0, 1 } }
            };

            var model = builder.Build(config);

            // 1. Verify Left Exterior Column on Front Gable has 3 broken segments: (0->2.2), (2.2->5.0), (5.0->7.0)
            var frontLeftColBeams = model.Beams.Values
                .Where(b => b.Type == MemberType.Column &&
                            Math.Abs(model.Nodes[b.NodeA].X) < 0.001 && Math.Abs(model.Nodes[b.NodeB].X) < 0.001 &&
                            Math.Abs(model.Nodes[b.NodeA].Z - 12.0) < 0.001 && Math.Abs(model.Nodes[b.NodeB].Z - 12.0) < 0.001)
                .ToList();
            Assert.Equal(3, frontLeftColBeams.Count);

            // 2. Verify Gable Post at X=6 on Front Gable has 3 broken segments: (0->2.2), (2.2->5.0), (5.0->7.6)
            var frontPostBeams = model.Beams.Values
                .Where(b => b.Type == MemberType.GablePost &&
                            Math.Abs(model.Nodes[b.NodeA].X - 6.0) < 0.001 && Math.Abs(model.Nodes[b.NodeB].X - 6.0) < 0.001 &&
                            Math.Abs(model.Nodes[b.NodeA].Z - 12.0) < 0.001 && Math.Abs(model.Nodes[b.NodeB].Z - 12.0) < 0.001)
                .ToList();
            Assert.Equal(3, frontPostBeams.Count);

            // 3. Verify Rafter on Front Gable is broken at each node: (0->6), (6->12), (12->18), (18->24)
            var frontRafterBeams = model.Beams.Values
                .Where(b => b.Type == MemberType.Rafter &&
                            Math.Abs(model.Nodes[b.NodeA].Z - 12.0) < 0.001 && Math.Abs(model.Nodes[b.NodeB].Z - 12.0) < 0.001)
                .ToList();
            Assert.Equal(4, frontRafterBeams.Count);
        }

        [Fact]
        public void Test_RoofStruts_AlongGablePosts_And_SubdividedRoofBracing()
        {
            var builder = new PortalGeometryBuilder();

            // 24m building with 4@6 gable posts (posts at X = 6, 12(ridge), 18)
            var config = new PortalConfiguration
            {
                ModelName = "Gable Post Struts Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 10.0) },
                BaySpacingExpression = "3@6", // 3 bays, 4 frames
                GableWalls = new GableWallConfiguration { GableBaySpacingFront = "4@6", GableBaySpacingRear = "4@6" },
                Bracing = new BracingConfiguration { IncludeRoofBracing = true, IncludeWallBracing = true, BracedBayIndices = new List<int> { 0, 2 } }
            };

            var model = builder.Build(config);

            // 1. Verify longitudinal struts exist along X = 0, 6, 12, 18, 24 across all 3 bays
            var strutBeams = model.Beams.Values
                .Where(b => b.Type == MemberType.EaveStrut || b.Type == MemberType.RidgeStrut)
                .ToList();

            // 5 strut lines * 3 bays = 15 strut members
            Assert.Equal(15, strutBeams.Count);

            // 2. Verify roof cross bracing is subdivided into 4 panels per braced bay
            // 4 panels * 2 diagonals = 8 roof braces per braced bay
            // 2 braced bays (0 and 2) => 16 roof braces total
            var roofBraceBeams = model.Beams.Values
                .Where(b => b.Type == MemberType.RoofBracing)
                .ToList();

            Assert.Equal(16, roofBraceBeams.Count);
        }

        [Fact]
        public void Test_RoofBracing_Max12_5m_Constraint_AutoSubdivision()
        {
            var builder = new PortalGeometryBuilder();

            // Wide 30m span with 8m bay spacing and NO gable posts (half span = 15m).
            // Without intermediate struts: diag = sqrt(15^2 + 1.5^2 + 8^2) = 17.06m > 12.5m!
            // With 12.5m rule: It must insert intermediate strut lines at X = 7.5m and X = 22.5m.
            var config = new PortalConfiguration
            {
                ModelName = "Wide 30m Warehouse",
                Spans = new List<SpanDefinition> { new SpanDefinition(30.0, 8.0, 10.0) },
                BaySpacingExpression = "2@8.0",
                GableWalls = new GableWallConfiguration { GableBaySpacingFront = "", GableBaySpacingRear = "" },
                Bracing = new BracingConfiguration
                {
                    IncludeRoofBracing = true,
                    MaxBraceLength = 12.5,
                    BracedBayIndices = new List<int> { 0, 1 }
                }
            };

            var model = builder.Build(config);

            // Verify EVERY roof cross brace diagonal length is <= 12.5m
            var roofBraceBeams = model.Beams.Values
                .Where(b => b.Type == MemberType.RoofBracing)
                .ToList();

            Assert.NotEmpty(roofBraceBeams);
            foreach (var brace in roofBraceBeams)
            {
                var nA = model.Nodes[brace.NodeA];
                var nB = model.Nodes[brace.NodeB];
                double dx = nB.X - nA.X;
                double dy = nB.Y - nA.Y;
                double dz = nB.Z - nA.Z;
                double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);

                Assert.True(len <= 12.501, $"Roof brace member {brace.Id} length {len:F2}m exceeds 12.5m maximum limit!");
            }
        }

        [Fact]
        public void Test_CableTray_LocationOptions_NoConnectingBeams()
        {
            var builder = new PortalGeometryBuilder();

            // Case 1: Side wall only
            var configSide = new PortalConfiguration
            {
                ModelName = "Side Only",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 10.0) },
                WidthModuleExpression = "2@12",
                BaySpacingExpression = "3@6",
                CableTray = new CableTrayConfiguration { Enabled = true, Location = "side", BracketHeight = 5.0, BracketLength = 0.8 }
            };
            var modelSide = builder.Build(configSide);
            var bracketsSide = modelSide.Beams.Values.Where(b => b.Type == MemberType.CableTrayBracket).ToList();
            Assert.Equal(8, bracketsSide.Count);
            Assert.DoesNotContain(modelSide.Beams.Values, b => b.Type == MemberType.CableTrayRunner);

            // Case 2: Side + Intermediate
            var configInt = new PortalConfiguration
            {
                ModelName = "Side + Intermediate",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 10.0) },
                WidthModuleExpression = "2@12",
                BaySpacingExpression = "3@6",
                CableTray = new CableTrayConfiguration { Enabled = true, Location = "side_intermediate", BracketHeight = 5.0, BracketLength = 0.8 }
            };
            var modelInt = builder.Build(configInt);
            var bracketsInt = modelInt.Beams.Values.Where(b => b.Type == MemberType.CableTrayBracket).ToList();
            Assert.Equal(10, bracketsInt.Count);
            Assert.DoesNotContain(modelInt.Beams.Values, b => b.Type == MemberType.CableTrayRunner);

            // Case 3: Side + Intermediate + Gable
            var configAll = new PortalConfiguration
            {
                ModelName = "Side + Intermediate + Gable",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 10.0) },
                WidthModuleExpression = "2@12",
                BaySpacingExpression = "3@6",
                GableWalls = new GableWallConfiguration { GableBaySpacingFront = "2@8", GableBaySpacingRear = "2@8" },
                CableTray = new CableTrayConfiguration { Enabled = true, Location = "all", BracketHeight = 5.0, BracketLength = 0.8 }
            };
            var modelAll = builder.Build(configAll);
            var bracketsAll = modelAll.Beams.Values.Where(b => b.Type == MemberType.CableTrayBracket).ToList();
            Assert.True(bracketsAll.Count >= 12);
            Assert.DoesNotContain(modelAll.Beams.Values, b => b.Type == MemberType.CableTrayRunner);
        }

        [Fact]
        public void Test_SupportTypes_FixedSideWall_PinnedIntermediateAndGable()
        {
            var builder = new PortalGeometryBuilder();

            // 24m warehouse: 3 bays (4 frames), 2@12 width modules (2 interior frames * 1 int col = 2 int cols), 4@6 gable posts (2 frames * 3 posts = 6 gable posts)
            // Side wall columns: 4 frames * 2 side columns = 8 fixed supports
            // Intermediate columns: 2 interior frames * 1 post = 2 pinned supports
            // Gable posts: 2 endwalls * 3 posts = 6 pinned supports
            var config = new PortalConfiguration
            {
                ModelName = "Supports Verification Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 10.0) },
                WidthModuleExpression = "2@12",
                BaySpacingExpression = "3@6",
                GableWalls = new GableWallConfiguration { GableBaySpacingFront = "4@6", GableBaySpacingRear = "4@6" }
            };

            var model = builder.Build(config);

            // 1. Verify Fixed Base Nodes (Side Wall Columns)
            Assert.Equal(8, model.FixedBaseNodeIds.Count);
            foreach (var nid in model.FixedBaseNodeIds)
            {
                var n = model.Nodes[nid];
                Assert.True(Math.Abs(n.X) < 0.001 || Math.Abs(n.X - 24.0) < 0.001, $"Node {nid} at X={n.X} should be on side wall for FIXED support");
            }

            // 2. Verify Pinned Base Nodes (Intermediate Columns + Gable Posts)
            Assert.Equal(8, model.PinnedBaseNodeIds.Count);
            foreach (var nid in model.PinnedBaseNodeIds)
            {
                var n = model.Nodes[nid];
                Assert.True(n.X > 0.01 && n.X < 23.99, $"Node {nid} at X={n.X} should be interior intermediate or gable post for PINNED support");
            }

            // 3. Verify STAAD .std output contains both FIXED and PINNED support commands
            string std = StaadStdWriter.GenerateStdText(model);
            Assert.Contains("SUPPORTS", std);
            Assert.Contains("FIXED", std);
            Assert.Contains("PINNED", std);
        }

        [Fact]
        public void Test_WallBracing_Max12_5m_Constraint_AutoSubdivision()
        {
            var builder = new PortalGeometryBuilder();

            // High eave (12m) with 8m bay spacing (Bay 0 braced, Bay 1 unbraced).
            // Without subdivision: diag = sqrt(12^2 + 8^2) = 14.42m > 12.5m!
            // With 12.5m rule:
            // 1. Subdivided into 2 tiers of 6m each (diag = sqrt(6^2 + 8^2) = 10.0m <= 12.5m).
            // 2. Wall struts at Y = 6.0m exist ONLY in Bay 0 (Z from 8m to 16m), NOT in Bay 1 (Z from 0m to 8m).
            var config = new PortalConfiguration
            {
                ModelName = "High Eave Wall Bracing Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(20.0, 12.0, 14.0) },
                BaySpacingExpression = "2@8", // Bay 0 (Z: 8->16), Bay 1 (Z: 0->8)
                Bracing = new BracingConfiguration
                {
                    IncludeRoofBracing = false,
                    IncludeWallBracing = true,
                    MaxBraceLength = 12.5,
                    BracedBayIndices = new List<int> { 0 }
                }
            };

            var model = builder.Build(config);

            // 1. Verify all wall bracing lengths are <= 12.5m
            var wallBraces = model.Beams.Values.Where(b => b.Type == MemberType.WallBracing).ToList();
            Assert.NotEmpty(wallBraces);
            // 2 side walls * 2 tiers * 2 diagonals = 8 wall braces
            Assert.Equal(8, wallBraces.Count);

            foreach (var brace in wallBraces)
            {
                var nA = model.Nodes[brace.NodeA];
                var nB = model.Nodes[brace.NodeB];
                double dy = nB.Y - nA.Y;
                double dz = nB.Z - nA.Z;
                double len = Math.Sqrt(dy * dy + dz * dz);
                Assert.True(len <= 12.501, $"Wall brace {brace.Id} length {len:F2}m exceeds 12.5m limit!");
            }

            // 2. Verify intermediate wall struts exist in Bay 0 at Y = 6.0m
            var wallStrutsBay0 = model.Beams.Values.Where(b =>
                b.GroupName == "WALL_STRUT" &&
                Math.Abs(model.Nodes[b.NodeA].Y - 6.0) < 0.01 &&
                Math.Abs(model.Nodes[b.NodeB].Y - 6.0) < 0.01).ToList();

            // 2 wall struts (Left wall at X=0, Right wall at X=20)
            Assert.Equal(2, wallStrutsBay0.Count);

            // 3. Verify NO wall struts in unbraced Bay 1 (Z from 0 to 8m)
            var wallStrutsBay1 = model.Beams.Values.Where(b =>
                b.GroupName == "WALL_STRUT" &&
                (Math.Abs(model.Nodes[b.NodeA].Z) < 0.01 || Math.Abs(model.Nodes[b.NodeB].Z) < 0.01)).ToList();
            Assert.Empty(wallStrutsBay1);
        }

        [Fact]
        public void Test_PortalBracing_HeaderBeam_KneeBraces_And_MemberBreaking()
        {
            var builder = new PortalGeometryBuilder();

            // 4 bays (5 frames): Bay 0, 1, 2, 3 (6m each). Total length = 24m.
            // Portal Bracing in Bay 2 at Height = 4.5m with 0.5m inner leg offset
            var config = new PortalConfiguration
            {
                ModelName = "Portal Bracing Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 9.4) },
                BaySpacingExpression = "4@6", // 24m length
                Bracing = new BracingConfiguration
                {
                    IncludeRoofBracing = false,
                    IncludeWallBracing = true,
                    BracedBayIndices = new List<int> { 0, 2 }, // Bay 0 standard wall brace, Bay 2 portal brace
                    EnablePortalBracing = true,
                    PortalBracingHeight = 4.5,
                    PortalLegOffset = 0.5,
                    PortalBracedBayIndices = new List<int> { 2 }
                }
            };

            var model = builder.Build(config);

            // 1. Verify Portal Header Beams (Left wall + Right wall = 2 lines, each subdivided with 0.5m offset inner nodes = 6 segments)
            var portalBeams = model.Beams.Values.Where(b => b.Type == MemberType.PortalBeam).ToList();
            Assert.Equal(6, portalBeams.Count);
            foreach (var pb in portalBeams)
            {
                var nA = model.Nodes[pb.NodeA];
                var nB = model.Nodes[pb.NodeB];
                Assert.True(Math.Abs(nA.Y - 4.5) < 0.01 && Math.Abs(nB.Y - 4.5) < 0.01, "Portal beam must be at Y = 4.5m");
            }

            // 2. Verify Portal Legs / Cross Lugs (2 on Left wall + 2 on Right wall = 4 inclined legs connecting 0.5m offset node to base)
            var portalLegs = model.Beams.Values.Where(b => b.Type == MemberType.PortalKneeBrace && b.GroupName == "PORTAL_LEG").ToList();
            Assert.Equal(4, portalLegs.Count);
            foreach (var leg in portalLegs)
            {
                var nA = model.Nodes[leg.NodeA];
                var nB = model.Nodes[leg.NodeB];
                double minY = Math.Min(nA.Y, nB.Y);
                double maxY = Math.Max(nA.Y, nB.Y);
                Assert.True(Math.Abs(minY - 0.0) < 0.01, "Portal leg must connect down to column base at Y = 0");
                Assert.True(Math.Abs(maxY - 4.5) < 0.01, "Portal leg must connect up to portal beam at Y = 4.5m");
            }

            // 3. Verify Upper Wall Cross-Bracing above Portal Beam (starts at Y = 4.5m up to Eave Y = 7.0m)
            var upperBracesInBay2 = model.Beams.Values.Where(b => b.Type == MemberType.WallBracing &&
                (Math.Abs(model.Nodes[b.NodeA].Y - 4.5) < 0.01 || Math.Abs(model.Nodes[b.NodeB].Y - 4.5) < 0.01)).ToList();
            // 2 side walls * 2 diagonals = 4 upper braces in Bay 2
            Assert.Equal(4, upperBracesInBay2.Count);
            foreach (var ub in upperBracesInBay2)
            {
                var nA = model.Nodes[ub.NodeA];
                var nB = model.Nodes[ub.NodeB];
                double minY = Math.Min(nA.Y, nB.Y);
                double maxY = Math.Max(nA.Y, nB.Y);
                Assert.True(Math.Abs(minY - 4.5) < 0.01, "Upper brace must start at portal beam level Y = 4.5m");
                Assert.True(Math.Abs(maxY - 7.0) < 0.01, "Upper brace must end at eave level Y = 7.0m");
            }

            // 4. Verify side columns at Frame 2 and Frame 3 are broken at Y = 4.5m (portal height)
            var colBeams = model.Beams.Values.Where(b => b.Type == MemberType.Column).ToList();
            var colNodesAt4_5 = colBeams.Select(b => model.Nodes[b.NodeA]).Concat(colBeams.Select(b => model.Nodes[b.NodeB]))
                .Where(n => Math.Abs(n.Y - 4.5) < 0.01).ToList();
            Assert.NotEmpty(colNodesAt4_5);

            // 5. Verify STAAD .std output contains ISMB350 and ISMC150 for portal members
            string std = StaadStdWriter.GenerateStdText(model);
            Assert.Contains("ISMB350", std);
            Assert.Contains("ISMC150", std);
        }

        [Fact]
        public void Test_PortalBracing_UpperCrossBracing_Subdivision_WhenDiagonalExceedsMax()
        {
            var builder = new PortalGeometryBuilder();

            // High eave height: 16m eave height, 4m portal height -> Upper Dy = 12m, BayZ = 8m -> Diagonal = 14.42m > 12.5m
            var config = new PortalConfiguration
            {
                ModelName = "Portal Subdivided Upper Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 16.0, 18.4) },
                BaySpacingExpression = "3@8", // 24m length
                Bracing = new BracingConfiguration
                {
                    IncludeRoofBracing = false,
                    IncludeWallBracing = true,
                    MaxBraceLength = 12.5,
                    EnablePortalBracing = true,
                    PortalBracingHeight = 4.0,
                    PortalLegOffset = 0.5,
                    BracedBayIndices = new List<int> { 1 },
                    PortalBracedBayIndices = new List<int> { 1 } // Bay 1
                }
            };

            var model = builder.Build(config);

            // 1. Upper Dy = 12m, Bay = 8m -> Subdivides into 2 tiers with subDy = 6m -> Wall strut at Y = 4 + 6 = 10m
            var upperWallStruts = model.Beams.Values.Where(b => b.Type == MemberType.EaveStrut && b.GroupName == "WALL_STRUT").ToList();
            Assert.NotEmpty(upperWallStruts);
            foreach (var ws in upperWallStruts)
            {
                var nA = model.Nodes[ws.NodeA];
                Assert.True(Math.Abs(nA.Y - 10.0) < 0.01, "Upper wall strut must be placed at intermediate tier Y = 10.0m");
            }

            // 2. Verify all upper cross braces in Bay 1 have length <= 12.5m
            var upperBraces = model.Beams.Values.Where(b => b.Type == MemberType.WallBracing).ToList();
            // 2 side walls * 2 tiers * 2 diagonals = 8 upper braces in Bay 1
            Assert.Equal(8, upperBraces.Count);
            foreach (var ub in upperBraces)
            {
                var nA = model.Nodes[ub.NodeA];
                var nB = model.Nodes[ub.NodeB];
                double length = Math.Sqrt(Math.Pow(nA.X - nB.X, 2) + Math.Pow(nA.Y - nB.Y, 2) + Math.Pow(nA.Z - nB.Z, 2));
                Assert.True(length <= 12.51, $"Upper brace length {length} must be <= 12.5m");
            }

            // 3. Verify side columns at Frame 1 and Frame 2 have nodes at Y = 4.0m, Y = 10.0m, and Y = 16.0m
            var colBeams = model.Beams.Values.Where(b => b.Type == MemberType.Column).ToList();
            var colNodes = colBeams.Select(b => model.Nodes[b.NodeA]).Concat(colBeams.Select(b => model.Nodes[b.NodeB])).ToList();
            Assert.Contains(colNodes, n => Math.Abs(n.Y - 4.0) < 0.01);
            Assert.Contains(colNodes, n => Math.Abs(n.Y - 10.0) < 0.01);
            Assert.Contains(colNodes, n => Math.Abs(n.Y - 16.0) < 0.01);
        }

        [Fact]
        public void Test_StaadStdWriter_GroupDefinition_And_MemberSpecification_Format()
        {
            var builder = new PortalGeometryBuilder();
            var config = new PortalConfiguration
            {
                ModelName = "STAAD Syntax Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(20.0, 7.0, 10.0) },
                BaySpacingExpression = "3@6",
                Bracing = new BracingConfiguration
                {
                    IncludeRoofBracing = true,
                    IncludeWallBracing = true,
                    BracedBayIndices = new List<int> { 0, 2 }
                }
            };

            var model = builder.Build(config);
            string std = StaadStdWriter.GenerateStdText(model);

            // 1. Check Group Definition format
            Assert.Contains("START GROUP DEFINITION", std);
            Assert.Contains("MEMBER\r\n_FRAME", std);
            Assert.Contains("_COLUMNS", std);
            Assert.Contains("_RAFTERS", std);
            Assert.Contains("_BRACING", std);
            Assert.Contains("END GROUP DEFINITION", std);

            // 2. Check Member Specification format (TENSION for cross bracing, TRUSS for strut pipes)
            Assert.Contains("******MEMBER SPECIFICATION", std);
            Assert.Contains("MEMBER TENSION", std);
            Assert.Contains("MEMBER TRUSS", std);
            Assert.Contains("G 7.88462e+07", std);
            Assert.Contains("STRENGTH RY 1.5 RT 1.2", std);
            Assert.NotEmpty(model.TensionOnlyBeamIds);
            Assert.NotEmpty(model.PinnedEndBeamIds);

            // 3. Check Range compression
            var sampleIds = new List<int> { 1, 2, 3, 4, 5, 8, 10, 11, 12 };
            var ranges = StaadStdWriter.CompressToStaadRanges(sampleIds);
            Assert.Contains("1 TO 5", ranges);
            Assert.Contains("8", ranges);
            Assert.Contains("10 TO 12", ranges);
        }

        [Fact]
        public void Test_PortalReleases_GablePostBeta90_And_GablePostRafterReleases()
        {
            var builder = new PortalGeometryBuilder();
            var config = new PortalConfiguration
            {
                ModelName = "Portal & Gable Release Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 8.0, 10.0) },
                BaySpacingExpression = "3@6",
                GableWalls = new GableWallConfiguration { GableBaySpacingFront = "3@8", GableBaySpacingRear = "3@8" },
                Bracing = new BracingConfiguration
                {
                    IncludeRoofBracing = true,
                    IncludeWallBracing = true,
                    EnablePortalBracing = true,
                    PortalBracingHeight = 4.0,
                    PortalLegOffset = 0.5,
                    BracedBayIndices = new List<int> { 1 },
                    PortalBracedBayIndices = new List<int> { 1 }
                }
            };

            var model = builder.Build(config);
            string std = StaadStdWriter.GenerateStdText(model);

            // 1. Verify BETA 90 MEMB assigned to Gable Posts under CONSTANTS
            Assert.NotEmpty(model.Beta90BeamIds);
            var gableBeamIds = model.Beams.Values.Where(b => b.Type == MemberType.GablePost).Select(b => b.Id).ToList();
            foreach (var gId in gableBeamIds)
            {
                Assert.Contains(gId, model.Beta90BeamIds);
            }
            Assert.Contains("BETA 90 MEMB", std);

            // 2. Verify MEMBER RELEASE block format
            Assert.Contains("MEMBER RELEASE", std);

            // 3. Verify Gable Post top releases connecting to rafter
            Assert.NotEmpty(model.EndReleaseMyMzBeamIds);
            Assert.Contains("END MY MZ", std);

            // 4. Verify Portal Beam releases at column connections
            Assert.NotEmpty(model.StartReleaseMyMzBeamIds);
            Assert.Contains("START MY MZ", std);

            // 5. Verify Portal Leg releases at base
            var portalLegs = model.Beams.Values.Where(b => b.Type == MemberType.PortalKneeBrace && b.GroupName == "PORTAL_LEG").ToList();
            Assert.NotEmpty(portalLegs);
            foreach (var leg in portalLegs)
            {
                Assert.Contains(leg.Id, model.EndReleaseMyMzBeamIds);
            }
        }

        [Fact]
        public void Test_JackPortal_IntermediateColumns_JackBeams_And_TieMembers()
        {
            var builder = new PortalGeometryBuilder();
            var config = new PortalConfiguration
            {
                ModelName = "Jack Portal Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 10.0) },
                WidthModuleExpression = "2@12", // Intermediate column line at X = 12m
                BaySpacingExpression = "6@6",  // Total 36m length with frames at Z = 0, 6, 12, 18, 24, 30, 36
                JackPortal = new JackPortalConfiguration
                {
                    Enabled = true,
                    IntermediateBaySpacingExpression = "3@12" // Columns along X = 12m only at Z = 0, 12, 24, 36
                }
            };

            var model = builder.Build(config);
            string std = StaadStdWriter.GenerateStdText(model);

            // 1. Verify intermediate columns exist: full columns at Z = 12 and 24, and stub posts at skipped frames Z = 6, 18, 30
            var intCols = model.Beams.Values.Where(b => b.Type == MemberType.IntermediateColumn).ToList();
            Assert.NotEmpty(intCols);

            // Full columns have base at Y = 0
            var fullColBaseNodes = intCols.Select(b => model.Nodes[b.NodeA]).Concat(intCols.Select(b => model.Nodes[b.NodeB]))
                .Where(n => Math.Abs(n.X - 12.0) < 0.05 && Math.Abs(n.Y - 0.0) < 0.05).Select(n => n.Z).Distinct().ToList();
            Assert.Contains(fullColBaseNodes, z => Math.Abs(z - 12.0) < 0.05);
            Assert.Contains(fullColBaseNodes, z => Math.Abs(z - 24.0) < 0.05);
            Assert.DoesNotContain(fullColBaseNodes, z => Math.Abs(z - 6.0) < 0.05);
            Assert.DoesNotContain(fullColBaseNodes, z => Math.Abs(z - 18.0) < 0.05);

            // 2. Verify Jack Beams exist 1m below rafter level (Y = 8.2 - 1.0 = 7.2)
            var jackBeams = model.Beams.Values.Where(b => b.Type == MemberType.JackBeam).ToList();
            Assert.NotEmpty(jackBeams);
            Assert.Contains("_JACKBEAMS", std);
            var jackNodes = jackBeams.Select(b => model.Nodes[b.NodeA]).Concat(jackBeams.Select(b => model.Nodes[b.NodeB])).ToList();
            double expectedJackY = 7.0 + 1.2 - 1.0; // Y_rafter(12m) - 1.0 = 8.2 - 1.0 = 7.2
            Assert.All(jackNodes, n => Assert.True(Math.Abs(n.Y - expectedJackY) < 0.1));

            // 3. Verify stub posts exist at skipped frames (Z = 6, 18, 30) from Y = 7.2 to Y = 8.2
            var stubPosts = model.Beams.Values.Where(b => b.GroupName == "JACK_POST").ToList();
            Assert.NotEmpty(stubPosts);
            var stubPostZ = stubPosts.Select(b => model.Nodes[b.NodeA].Z).Distinct().ToList();
            Assert.Contains(stubPostZ, z => Math.Abs(z - 6.0) < 0.05);
            Assert.Contains(stubPostZ, z => Math.Abs(z - 18.0) < 0.05);
            Assert.Contains(stubPostZ, z => Math.Abs(z - 30.0) < 0.05);

            // 4. Verify releases: stub posts have BOTH Start and End releases (top and bottom)
            foreach (var sp in stubPosts)
            {
                Assert.Contains(sp.Id, model.StartReleaseMyMzBeamIds);
                Assert.Contains(sp.Id, model.EndReleaseMyMzBeamIds);
            }

            // 5. Verify full columns have top release connecting to rafter
            Assert.NotEmpty(model.EndReleaseMyMzBeamIds);

            // 6. Verify ASSIGN COLUMN and _JACKPOSTS in STAAD output
            Assert.Contains("ASSIGN COLUMN", std);
            Assert.Contains("_JACKPOSTS", std);

            // 7. Verify Jack Beams are NOT released
            foreach (var jb in jackBeams)
            {
                Assert.DoesNotContain(jb.Id, model.StartReleaseMyMzBeamIds);
                Assert.DoesNotContain(jb.Id, model.EndReleaseMyMzBeamIds);
            }

            // 8. Verify Jack Portal intermediate columns and stub posts have BETA 90
            foreach (var col in intCols)
            {
                Assert.Contains(col.Id, model.Beta90BeamIds);
            }
            Assert.Contains("BETA 90 MEMB", std);
        }

        [Fact]
        public void Test_JackPortal_NoJackBeam_When_Columns_At_Each_Frame()
        {
            var builder = new PortalGeometryBuilder();
            var config = new PortalConfiguration
            {
                ModelName = "Jack Portal No Skipped Frames Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 10.0) },
                WidthModuleExpression = "2@12",
                BaySpacingExpression = "6@6",
                JackPortal = new JackPortalConfiguration
                {
                    Enabled = true,
                    IntermediateBaySpacingExpression = "6@6" // Columns at every frame (no skipped frames)
                }
            };

            var model = builder.Build(config);

            // 1. Verify no Jack Beams are generated since columns exist at every frame
            var jackBeams = model.Beams.Values.Where(b => b.Type == MemberType.JackBeam).ToList();
            Assert.Empty(jackBeams);

            // 2. Verify no dummy 1m-below-rafter nodes exist on columns
            double expectedJackY = 7.0 + 1.2 - 1.0; // 7.2m
            var intColNodes = model.Nodes.Values.Where(n => Math.Abs(n.X - 12.0) < 0.05).ToList();
            Assert.DoesNotContain(intColNodes, n => Math.Abs(n.Y - expectedJackY) < 0.05);

            // 3. Verify no top releases on intermediate columns
            var intColBeams = model.Beams.Values.Where(b => b.Type == MemberType.IntermediateColumn).Select(b => b.Id).ToList();
            foreach (var bid in intColBeams)
            {
                Assert.DoesNotContain(bid, model.EndReleaseMyMzBeamIds);
                Assert.DoesNotContain(bid, model.StartReleaseMyMzBeamIds);
                Assert.DoesNotContain(bid, model.Beta90BeamIds);
            }
        }

        [Fact]
        public void Test_JackPortal_FrontToRear_Layout()
        {
            var builder = new PortalGeometryBuilder();
            var config = new PortalConfiguration
            {
                ModelName = "Jack Portal Front To Rear Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 10.0) },
                WidthModuleExpression = "2@12", // Intermediate column line at X = 12m
                BaySpacingExpression = "5@6+5@4",  // Total 50m length (Front Z=50 down to Rear Z=0)
                JackPortal = new JackPortalConfiguration
                {
                    Enabled = true,
                    IntermediateBaySpacingExpression = "2@8+1@10+4@6" // Stepping from Front: Z = 50, 42, 34, 24, 18, 12, 6, 0
                }
            };

            var model = builder.Build(config);
            string std = StaadStdWriter.GenerateStdText(model);

            // Verify intermediate columns exist along X = 12m at Front-to-Rear coordinates
            var intCols = model.Beams.Values.Where(b => b.Type == MemberType.IntermediateColumn).ToList();
            Assert.NotEmpty(intCols);

            // Jack Beams exist 1m below rafter level
            var jackBeams = model.Beams.Values.Where(b => b.Type == MemberType.JackBeam).ToList();
            Assert.NotEmpty(jackBeams);
            Assert.Contains("_JACKBEAMS", std);

            // Stub posts have ASSIGN COLUMN
            Assert.Contains("ASSIGN COLUMN", std);
        }

        [Fact]
        public void Test_BaySpacing_Zero_ReturnsEmptyList()
        {
            var bays0 = SpacingParser.ParseBaySpacings("0");
            Assert.Empty(bays0);

            var bays0Dot0 = SpacingParser.ParseBaySpacings("0.0");
            Assert.Empty(bays0Dot0);
        }

        [Fact]
        public void Test_BaySpacing_Zero_GeneratesSingleMidFrame()
        {
            var builder = new PortalGeometryBuilder();
            var config = new PortalConfiguration
            {
                ModelName = "Single Mid Frame 50m Span",
                Spans = new List<SpanDefinition> { new SpanDefinition(50.0, 15.0, 5.0) },
                WidthModuleExpression = "3", // 3 intermediate columns at X = 12.5, 25.0, 37.5
                BaySpacingExpression = "0", // ZERO BAYS -> single mid frame
                RccWallHeight = 3.0,
                GableWalls = new GableWallConfiguration { GableBaySpacingFront = "3", GableBaySpacingRear = "3" },
                CableTray = new CableTrayConfiguration { Enabled = true, Location = "side_intermediate", BracketHeight = 5.0, BracketLength = 0.8 }
            };

            var model = builder.Build(config);

            Assert.NotNull(model);
            Assert.Equal(50.0, config.TotalWidth);
            Assert.Equal(0.0, config.TotalLength);
            Assert.Equal(0, config.TotalBays);
            Assert.Equal(1, config.TotalFrames);

            // 1. Verify all generated nodes are situated at Z = 0.0
            Assert.NotEmpty(model.Nodes);
            foreach (var node in model.Nodes.Values)
            {
                Assert.True(Math.Abs(node.Z) < 0.001, $"Node {node.Id} should be at Z=0, but got Z={node.Z}");
            }

            // 2. Verify NO gable wind posts exist (it is a mid frame, not a gable endwall)
            var gablePosts = model.Beams.Values.Where(b => b.Type == MemberType.GablePost).ToList();
            Assert.Empty(gablePosts);

            // 3. Verify intermediate columns DO exist on the mid frame
            var intCols = model.Beams.Values.Where(b => b.Type == MemberType.IntermediateColumn).ToList();
            Assert.NotEmpty(intCols);
            var intColXCoords = intCols.Select(b => model.Nodes[b.NodeA].X).Distinct().OrderBy(x => x).ToList();
            Assert.Contains(intColXCoords, x => Math.Abs(x - 12.5) < 0.1);
            Assert.Contains(intColXCoords, x => Math.Abs(x - 25.0) < 0.1);
            Assert.Contains(intColXCoords, x => Math.Abs(x - 37.5) < 0.1);

            // 4. Verify exterior columns exist at X = 0 and X = 50
            var extCols = model.Beams.Values.Where(b => b.Type == MemberType.Column).ToList();
            Assert.NotEmpty(extCols);
            var extColXCoords = extCols.Select(b => model.Nodes[b.NodeA].X).Distinct().OrderBy(x => x).ToList();
            Assert.Contains(extColXCoords, x => Math.Abs(x - 0.0) < 0.1);
            Assert.Contains(extColXCoords, x => Math.Abs(x - 50.0) < 0.1);

            // 5. Verify NO longitudinal members exist (no struts, no bracings, no jack beams)
            Assert.DoesNotContain(model.Beams.Values, b => b.Type == MemberType.EaveStrut || b.Type == MemberType.RidgeStrut);
            Assert.DoesNotContain(model.Beams.Values, b => b.Type == MemberType.RoofBracing || b.Type == MemberType.WallBracing);
            Assert.DoesNotContain(model.Beams.Values, b => b.Type == MemberType.JackBeam);

            // 6. Verify Supports: Exterior columns FIXED, Intermediate columns PINNED
            Assert.Equal(2, model.FixedBaseNodeIds.Count);
            Assert.Equal(3, model.PinnedBaseNodeIds.Count);

            // 7. Verify STAAD .std output
            string std = StaadStdWriter.GenerateStdText(model);
            Assert.Contains("STAAD SPACE", std);
            Assert.Contains("JOINT COORDINATES", std);
            Assert.Contains("MEMBER INCIDENCES", std);
            Assert.Contains("SUPPORTS", std);
            Assert.Contains("FIXED", std);
            Assert.Contains("PINNED", std);
            Assert.Contains("FINISH", std);
        }

        [Fact]
        public void Test_Convert2DTo3D_PreservesOptimizedPropertiesAndAddsBracingAndGables()
        {
            string std2D = @"STAAD SPACE
START JOB INFORMATION
ENGINEER DATE 02-Sep-26
JOB NAME 2D Tapered PEB Frame
END JOB INFORMATION
INPUT WIDTH 79
UNIT METER KN
JOINT COORDINATES
1 0.0000 0.0000 0.0000;
2 0.0000 7.0000 0.0000;
3 12.0000 8.2000 0.0000;
4 24.0000 7.0000 0.0000;
5 24.0000 0.0000 0.0000;
MEMBER INCIDENCES
1 1 2;
2 2 3;
3 3 4;
4 5 4;
MEMBER PROPERTY INDIAN
1 4 TAPERED 0.40 0.010 0.25 0.012 0.75 0.25 0.012
2 3 TAPERED 0.75 0.010 0.25 0.012 0.40 0.25 0.012
SUPPORTS
1 5 FIXED
PERFORM ANALYSIS
FINISH";

            var builder = new PortalGeometryBuilder();
            var config = new PortalConfiguration
            {
                ModelName = "Converted 3D PEB Shed",
                GenerationMode = "convert2d",
                Template2DStdText = std2D,
                BaySpacingExpression = "3@6.0", // 3 bays = 4 frames at Z = 18, 12, 6, 0
                Bracing = new BracingConfiguration
                {
                    IncludeRoofBracing = true,
                    IncludeWallBracing = true,
                    BracedBayIndices = new List<int> { 0, 2 }
                },
                GableWalls = new GableWallConfiguration
                {
                    GableBaySpacingFront = "3", // 3 posts
                    GableBaySpacingRear = "3"
                }
            };

            var model = builder.Build(config);

            Assert.NotNull(model);
            Assert.Equal(4, model.Configuration.TotalFrames);
            Assert.Equal(18.0, model.Configuration.TotalLength);

            // 1. Verify 4 portal frames exist along Z = 18, 12, 6, 0
            var distinctZ = model.Nodes.Values.Select(n => Math.Round(n.Z, 2)).Distinct().OrderByDescending(z => z).ToList();
            Assert.Contains(distinctZ, z => Math.Abs(z - 18.0) < 0.1);
            Assert.Contains(distinctZ, z => Math.Abs(z - 12.0) < 0.1);
            Assert.Contains(distinctZ, z => Math.Abs(z - 6.0) < 0.1);
            Assert.Contains(distinctZ, z => Math.Abs(z - 0.0) < 0.1);

            // 2. Verify all 4 frames have their columns (4 frames * 2 cols = 8) and rafters
            // Note: Since rafters are broken at intermediate joints (gable posts/struts at X=6 and X=18),
            // each half-span rafter is broken into 2 segments (4 rafter segments per frame * 4 frames = 16)
            var columns = model.Beams.Values.Where(b => b.Type == MemberType.Column).ToList();
            var rafters = model.Beams.Values.Where(b => b.Type == MemberType.Rafter).ToList();
            Assert.Equal(8, columns.Count);
            Assert.Equal(16, rafters.Count);

            // 3. Verify Tapered sections are preserved on columns and rafters
            foreach (var col in columns)
            {
                Assert.Contains("TAPERED", col.SectionProperty);
            }
            foreach (var raf in rafters)
            {
                Assert.Contains("TAPERED", raf.SectionProperty);
            }

            // 4. Verify Longitudinal Struts exist (eaves and ridge struts)
            var struts = model.Beams.Values.Where(b => b.Type == MemberType.EaveStrut || b.Type == MemberType.RidgeStrut).ToList();
            Assert.NotEmpty(struts);

            // 5. Verify Roof & Wall X-bracing exist
            var roofBracing = model.Beams.Values.Where(b => b.Type == MemberType.RoofBracing).ToList();
            var wallBracing = model.Beams.Values.Where(b => b.Type == MemberType.WallBracing).ToList();
            Assert.NotEmpty(roofBracing);
            Assert.NotEmpty(wallBracing);

            // 6. Verify Gable wind posts exist on Front (Z=18) and Rear (Z=0) frames
            var gablePosts = model.Beams.Values.Where(b => b.Type == MemberType.GablePost).ToList();
            Assert.NotEmpty(gablePosts);
            var gableZ = gablePosts.Select(b => Math.Round(model.Nodes[b.NodeA].Z, 2)).Distinct().ToList();
            Assert.Contains(gableZ, z => Math.Abs(z - 18.0) < 0.1);
            Assert.Contains(gableZ, z => Math.Abs(z - 0.0) < 0.1);

            // 7. Verify STAAD .std output contains preserved tapered properties and added bracing/strut properties
            string std = StaadStdWriter.GenerateStdText(model);
            Assert.Contains("TAPERED", std);
            Assert.Contains("ISA65X65X6", std);
            Assert.Contains("ISMC150", std);
            Assert.Contains("ISMB250", std);
            Assert.Contains("_FRAME", std);
            Assert.Contains("_ROOFBRACING", std);
            Assert.Contains("_WALLBRACING", std);
            Assert.Contains("_GABLEPOSTS", std);
            Assert.Contains("MEMBER TENSION", std);
            Assert.Contains("SUPPORTS", std);
            Assert.Contains("FIXED", std);
            Assert.Contains("PINNED", std);
        }

        [Fact]
        public void Test_Convert2DTo3D_AdvancedStructuralRules()
        {
            // 2D Model with:
            // 1. RCC wall node at Y=2.0 on left column (Node 2)
            // 2. Intermediate frame column at X=12.0 (Node 10 to Node 5)
            // 3. Canopy cantilever on right column at Y=4.5 (Node 8 to Node 11 at X=27)
            string std2D = @"STAAD SPACE
START JOB INFORMATION
ENGINEER DATE 03-Sep-26
JOB NAME 2D Advanced Structural PEB
END JOB INFORMATION
INPUT WIDTH 79
UNIT METER KN
JOINT COORDINATES
1 0.0000 0.0000 0.0000;
2 0.0000 2.0000 0.0000;
3 0.0000 7.0000 0.0000;
4 6.0000 7.6000 0.0000;
5 12.0000 8.2000 0.0000;
6 18.0000 7.6000 0.0000;
7 24.0000 7.0000 0.0000;
8 24.0000 4.5000 0.0000;
9 24.0000 0.0000 0.0000;
10 12.0000 0.0000 0.0000;
11 27.0000 4.5000 0.0000;
MEMBER INCIDENCES
1 1 2;
2 2 3;
3 3 4;
4 4 5;
5 5 6;
6 6 7;
7 9 8;
8 8 7;
9 10 5;
10 8 11;
MEMBER PROPERTY INDIAN
1 TO 2 7 TO 8 TAPERED 0.40 0.010 0.25 0.012 0.75 0.25 0.012
3 TO 6 TAPERED 0.75 0.010 0.25 0.012 0.45 0.25 0.012
9 TABLE ST ISMB350
10 TABLE ST ISMB200
SUPPORTS
1 9 FIXED
10 PINNED
PERFORM ANALYSIS
FINISH";

            var builder = new PortalGeometryBuilder();
            var config = new PortalConfiguration
            {
                ModelName = "Advanced Converted 3D Shed",
                GenerationMode = "convert2d",
                Template2DStdText = std2D,
                BaySpacingExpression = "3@6.0", // 4 frames at Z = 18, 12, 6, 0
                Bracing = new BracingConfiguration
                {
                    IncludeRoofBracing = true,
                    IncludeWallBracing = true,
                    MaxBraceLength = 12.5,
                    BracedBayIndices = new List<int> { 0, 2 }
                },
                GableWalls = new GableWallConfiguration
                {
                    GableBaySpacingFront = "3",
                    GableBaySpacingRear = "3"
                }
            };

            var model = builder.Build(config);
            Assert.NotNull(model);

            // Rule 2: Wall height auto-detected from 1st node above base (Y = 2.0)
            Assert.Equal(2.0, model.Configuration.RccWallHeight);

            // Rule 1: 2D Intermediate column at X=12 is REMOVED from front (Z=18) and rear (Z=0) gable frames,
            // but PRESENT on interior frames (Z=12, 6)
            var intColBeams = model.Beams.Values.Where(b =>
            {
                var nA = model.Nodes[b.NodeA];
                var nB = model.Nodes[b.NodeB];
                return Math.Abs(nA.X - 12.0) < 0.15 && Math.Abs(nB.X - 12.0) < 0.15 && Math.Min(nA.Y, nB.Y) < 0.5 && b.Type != MemberType.GablePost;
            }).ToList();

            // Interior frames (Z=12 and Z=6) have interior columns
            var intColZ = intColBeams.Select(b => Math.Round(model.Nodes[b.NodeA].Z, 2)).Distinct().ToList();
            Assert.Contains(intColZ, z => Math.Abs(z - 12.0) < 0.1);
            Assert.Contains(intColZ, z => Math.Abs(z - 6.0) < 0.1);

            // Front (Z=18) and rear (Z=0) gable frames DO NOT have the 2D intermediate framed column
            Assert.DoesNotContain(intColZ, z => Math.Abs(z - 18.0) < 0.1);
            Assert.DoesNotContain(intColZ, z => Math.Abs(z - 0.0) < 0.1);

            // Rule 3: End gable posts exist and have longitudinal strut pipes connecting their tops into the building
            var gablePosts = model.Beams.Values.Where(b => b.Type == MemberType.GablePost).ToList();
            Assert.NotEmpty(gablePosts);
            foreach (var gp in gablePosts)
            {
                var topNode = model.Nodes[gp.NodeB];
                // Check longitudinal strut attached to topNode
                var connectedStruts = model.Beams.Values.Where(b =>
                    (b.Type == MemberType.EaveStrut || b.Type == MemberType.RidgeStrut) &&
                    (b.NodeA == topNode.Id || b.NodeB == topNode.Id)).ToList();
                Assert.NotEmpty(connectedStruts);
            }

            // Rule 3: Roof bracing is modeled in panels between strut lines
            var roofBracing = model.Beams.Values.Where(b => b.Type == MemberType.RoofBracing).ToList();
            Assert.NotEmpty(roofBracing);

            // Rule 4: Canopy cross-bracing rule: On the right wall (X=24) where canopy exists at Y=4.5,
            // cross bracing is modeled BOTH below canopy (Y <= 4.5) AND above canopy (Y >= 4.5)
            var rightWallBracing = model.Beams.Values.Where(b =>
            {
                var nA = model.Nodes[b.NodeA];
                var nB = model.Nodes[b.NodeB];
                return b.Type == MemberType.WallBracing && Math.Abs(nA.X - 24.0) < 0.15 && Math.Abs(nB.X - 24.0) < 0.15;
            }).ToList();

            Assert.NotEmpty(rightWallBracing);
            // Verify bracing below canopy (starts at base Y ~ 0)
            Assert.Contains(rightWallBracing, b =>
            {
                var nA = model.Nodes[b.NodeA];
                var nB = model.Nodes[b.NodeB];
                return Math.Min(nA.Y, nB.Y) < 1.0;
            });
            // Verify bracing above canopy (reaches eave Y > 4.5)
            Assert.Contains(rightWallBracing, b =>
            {
                var nA = model.Nodes[b.NodeA];
                var nB = model.Nodes[b.NodeB];
                return Math.Max(nA.Y, nB.Y) > 5.0;
            });

            // Canopy continuous runners connecting cantilever tips along building length
            var canopyRunners = model.Beams.Values.Where(b => b.GroupName == "CANOPY_RUNNER").ToList();
            Assert.NotEmpty(canopyRunners);

            // Canopy plan cross-bracing in braced bays
            var canopyBracing = model.Beams.Values.Where(b => b.GroupName == "CANOPY_BRACE").ToList();
            Assert.NotEmpty(canopyBracing);

            // Left wall (no canopy): Cross bracing starts from foundation base (Y = 0)
            var leftWallBracing = model.Beams.Values.Where(b =>
            {
                var nA = model.Nodes[b.NodeA];
                var nB = model.Nodes[b.NodeB];
                return b.Type == MemberType.WallBracing && Math.Abs(nA.X - 0.0) < 0.15 && Math.Abs(nB.X - 0.0) < 0.15;
            }).ToList();

            Assert.NotEmpty(leftWallBracing);
            var leftBaseNodes = leftWallBracing.SelectMany(b => new[] { model.Nodes[b.NodeA], model.Nodes[b.NodeB] })
                .Where(n => Math.Abs(n.Y) < 0.1).ToList();
            Assert.NotEmpty(leftBaseNodes);

            // Rule 5: Check all braces respect MaxBraceLength (12.5m)
            var allBracing = model.Beams.Values.Where(b => b.Type == MemberType.RoofBracing || b.Type == MemberType.WallBracing).ToList();
            foreach (var br in allBracing)
            {
                var nA = model.Nodes[br.NodeA];
                var nB = model.Nodes[br.NodeB];
                double dx = nB.X - nA.X;
                double dy = nB.Y - nA.Y;
                double dz = nB.Z - nA.Z;
                double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                Assert.True(len <= 12.55, $"Brace {br.Id} length {len:F2}m exceeds max 12.5m");
            }

            // Verification of Requirement 2: Horizontal member along wall height is REMOVED
            Assert.DoesNotContain(model.Beams.Values, b => b.Type == MemberType.RccWallTieBeam);

            // Verification of Requirement 3: Canopy side exterior column IS present on Front (Z=18) and Rear (Z=0) gable frames
            var rightColBeams = model.Beams.Values.Where(b =>
            {
                var nA = model.Nodes[b.NodeA];
                var nB = model.Nodes[b.NodeB];
                return Math.Abs(nA.X - 24.0) < 0.15 && Math.Abs(nB.X - 24.0) < 0.15 && b.Type != MemberType.WallBracing;
            }).ToList();
            var rightColZ = rightColBeams.Select(b => Math.Round(model.Nodes[b.NodeA].Z, 2)).Distinct().ToList();
            Assert.Contains(rightColZ, z => Math.Abs(z - 18.0) < 0.1);
            Assert.Contains(rightColZ, z => Math.Abs(z - 0.0) < 0.1);

            // Verification of Requirement 3: Wall bracing IS added on canopy side between front gable and adjacent frame
            var bay0RightBracing = rightWallBracing.Where(b =>
            {
                var nA = model.Nodes[b.NodeA];
                var nB = model.Nodes[b.NodeB];
                return (Math.Abs(nA.Z - 18.0) < 0.1 && Math.Abs(nB.Z - 12.0) < 0.1) ||
                       (Math.Abs(nA.Z - 12.0) < 0.1 && Math.Abs(nB.Z - 18.0) < 0.1);
            }).ToList();
            Assert.NotEmpty(bay0RightBracing);
        }

        [Fact]
        public void Test_Convert2DTo3D_JackPortal_IntermediateColumns_JackBeams_And_Releases()
        {
            string std2DWithIntCol = @"STAAD SPACE
START JOB INFORMATION
ENGINEER DATE 04-Sep-26
JOB NAME 2D Frame with Intermediate Column
END JOB INFORMATION
INPUT WIDTH 79
UNIT METER KN
JOINT COORDINATES
1 0.0000 0.0000 0.0000;
2 0.0000 7.0000 0.0000;
3 12.0000 0.0000 0.0000;
4 12.0000 8.2000 0.0000;
5 24.0000 7.0000 0.0000;
6 24.0000 0.0000 0.0000;
MEMBER INCIDENCES
1 1 2;
2 2 4;
3 4 5;
4 6 5;
5 3 4;
MEMBER PROPERTY INDIAN
1 4 TAPERED 0.40 0.010 0.25 0.012 0.75 0.25 0.012
2 3 TAPERED 0.75 0.010 0.25 0.012 0.40 0.25 0.012
5 TABLE ST ISMB400
SUPPORTS
1 6 FIXED
3 PINNED
PERFORM ANALYSIS
FINISH";

            var builder = new PortalGeometryBuilder();
            var config = new PortalConfiguration
            {
                ModelName = "2D to 3D with Jack Portal",
                GenerationMode = "convert2d",
                Template2DStdText = std2DWithIntCol,
                BaySpacingExpression = "6@6.0", // Z = 36, 30, 24, 18, 12, 6, 0 (Total 36m, 7 frames)
                JackPortal = new JackPortalConfiguration
                {
                    Enabled = true,
                    IntermediateBaySpacingExpression = "3@12.0" // Intermediate cols at Z = 36, 24, 12, 0. Skipped at Z = 30, 18, 6
                }
            };

            var model = builder.Build(config);
            string std = StaadStdWriter.GenerateStdText(model);

            // 1. Verify intermediate columns exist: full columns on interior frames at Z = 24, 12; skipped at Z = 30, 18, 6
            var intCols = model.Beams.Values.Where(b => b.Type == MemberType.IntermediateColumn).ToList();
            Assert.NotEmpty(intCols);

            var fullColBaseNodes = intCols.Select(b => model.Nodes[b.NodeA]).Concat(intCols.Select(b => model.Nodes[b.NodeB]))
                .Where(n => Math.Abs(n.X - 12.0) < 0.05 && Math.Abs(n.Y - 0.0) < 0.05).Select(n => n.Z).Distinct().ToList();
            Assert.Contains(fullColBaseNodes, z => Math.Abs(z - 24.0) < 0.05);
            Assert.Contains(fullColBaseNodes, z => Math.Abs(z - 12.0) < 0.05);
            Assert.DoesNotContain(fullColBaseNodes, z => Math.Abs(z - 30.0) < 0.05);
            Assert.DoesNotContain(fullColBaseNodes, z => Math.Abs(z - 18.0) < 0.05);
            Assert.DoesNotContain(fullColBaseNodes, z => Math.Abs(z - 6.0) < 0.05);

            // 2. Verify Jack Beams exist 1m below rafter level (Y = 8.2 - 1.0 = 7.2)
            var jackBeams = model.Beams.Values.Where(b => b.Type == MemberType.JackBeam).ToList();
            Assert.NotEmpty(jackBeams);
            Assert.Contains("_JACKBEAMS", std);
            Assert.Contains("TABLE ST ISMB500", std);

            var jackNodes = jackBeams.Select(b => model.Nodes[b.NodeA]).Concat(jackBeams.Select(b => model.Nodes[b.NodeB])).ToList();
            double expectedJackY = 8.2 - 1.0; // 7.2m
            Assert.All(jackNodes, n => Assert.True(Math.Abs(n.Y - expectedJackY) < 0.15));

            // 3. Verify stub posts exist at skipped frames (Z = 30, 18, 6) from Y = 7.2 to Y = 8.2
            var stubPosts = model.Beams.Values.Where(b => b.GroupName == "JACK_POST").ToList();
            Assert.NotEmpty(stubPosts);
            var stubPostZ = stubPosts.Select(b => model.Nodes[b.NodeA].Z).Distinct().ToList();
            Assert.Contains(stubPostZ, z => Math.Abs(z - 30.0) < 0.05);
            Assert.Contains(stubPostZ, z => Math.Abs(z - 18.0) < 0.05);
            Assert.Contains(stubPostZ, z => Math.Abs(z - 6.0) < 0.05);

            // 4. Verify releases: stub posts have BOTH Start and End releases
            foreach (var sp in stubPosts)
            {
                Assert.Contains(sp.Id, model.StartReleaseMyMzBeamIds);
                Assert.Contains(sp.Id, model.EndReleaseMyMzBeamIds);
            }

            // 5. Verify full intermediate columns have top release connecting to rafter
            Assert.NotEmpty(model.EndReleaseMyMzBeamIds);

            // 6. Verify ASSIGN COLUMN and _JACKPOSTS in STAAD output
            Assert.Contains("ASSIGN COLUMN", std);
            Assert.Contains("_JACKPOSTS", std);

            // 7. Verify Jack Portal intermediate columns and stub posts have BETA 90
            foreach (var col in intCols)
            {
                Assert.Contains(col.Id, model.Beta90BeamIds);
            }
            Assert.Contains("BETA 90 MEMB", std);
        }

        [Fact]
        public void Test_Convert2DTo3D_CableTray_SideIntermediateAndAllLocations()
        {
            string std2DWithIntCol = @"STAAD SPACE
START JOB INFORMATION
ENGINEER DATE 04-Sep-26
JOB NAME 2D Frame with Intermediate Column
END JOB INFORMATION
INPUT WIDTH 79
UNIT METER KN
JOINT COORDINATES
1 0.0000 0.0000 0.0000;
2 0.0000 7.0000 0.0000;
3 12.0000 0.0000 0.0000;
4 12.0000 8.2000 0.0000;
5 24.0000 7.0000 0.0000;
6 24.0000 0.0000 0.0000;
MEMBER INCIDENCES
1 1 2;
2 2 4;
3 4 5;
4 6 5;
5 3 4;
MEMBER PROPERTY INDIAN
1 4 TAPERED 0.40 0.010 0.25 0.012 0.75 0.25 0.012
2 3 TAPERED 0.75 0.010 0.25 0.012 0.40 0.25 0.012
5 TABLE ST ISMB400
SUPPORTS
1 6 FIXED
3 PINNED
PERFORM ANALYSIS
FINISH";

            var builder = new PortalGeometryBuilder();

            // Test 1: Location = "side_intermediate"
            var configInt = new PortalConfiguration
            {
                ModelName = "2D to 3D Cable Tray Side+Int",
                GenerationMode = "convert2d",
                Template2DStdText = std2DWithIntCol,
                BaySpacingExpression = "2@6.0", // 3 frames: Z = 12, 6, 0
                CableTray = new CableTrayConfiguration
                {
                    Enabled = true,
                    Location = "side_intermediate",
                    BracketHeight = 4.5,
                    BracketLength = 0.7
                }
            };

            var modelInt = builder.Build(configInt);
            string stdInt = StaadStdWriter.GenerateStdText(modelInt);

            // Verify brackets exist
            var bracketsInt = modelInt.Beams.Values.Where(b => b.Type == MemberType.CableTrayBracket).ToList();
            Assert.NotEmpty(bracketsInt);

            // Left side (X=0), Right side (X=24), Intermediate (X=12) brackets exist
            var bracketXs = bracketsInt.Select(b => Math.Round(Math.Min(modelInt.Nodes[b.NodeA].X, modelInt.Nodes[b.NodeB].X), 2)).Distinct().ToList();
            Assert.Contains(bracketXs, x => Math.Abs(x - 0.0) < 0.05);
            Assert.Contains(bracketXs, x => Math.Abs(x - 12.0) < 0.05);
            Assert.Contains(bracketXs, x => Math.Abs(x - 23.3) < 0.05 || Math.Abs(x - 24.0) < 0.05);

            // Verify bracket Y is 4.5
            Assert.All(bracketsInt, b => Assert.True(Math.Abs(modelInt.Nodes[b.NodeA].Y - 4.5) < 0.05));

            // Verify STAAD output has _CABLETRAY and ISMC100
            Assert.Contains("_CABLETRAY", stdInt);
            Assert.Contains("TABLE ST ISMC100", stdInt);

            // Test 2: Location = "all" with gable posts
            var configAll = new PortalConfiguration
            {
                ModelName = "2D to 3D Cable Tray All",
                GenerationMode = "convert2d",
                Template2DStdText = std2DWithIntCol,
                BaySpacingExpression = "2@6.0",
                GableWalls = new GableWallConfiguration
                {
                    GableBaySpacingFront = "3",
                    GableBaySpacingRear = "3"
                },
                CableTray = new CableTrayConfiguration
                {
                    Enabled = true,
                    Location = "all",
                    BracketHeight = 4.5,
                    BracketLength = 0.7
                }
            };

            var modelAll = builder.Build(configAll);
            var bracketsAll = modelAll.Beams.Values.Where(b => b.Type == MemberType.CableTrayBracket).ToList();
            Assert.True(bracketsAll.Count > bracketsInt.Count, "Location 'all' should generate more brackets than 'side_intermediate' due to gable posts");
        }

        [Fact]
        public void Test_Convert2DTo3D_PortalBracing()
        {
            string std2D = @"STAAD SPACE
START JOB INFORMATION
ENGINEER DATE 28-Feb-25
END JOB INFORMATION
INPUT WIDTH 79
UNIT METER KN
JOINT COORDINATES
1 0.0000 0.0000 0.0000;
2 0.0000 2.2000 0.0000;
3 0.0000 7.0000 0.0000;
4 12.0000 8.2000 0.0000;
5 24.0000 7.0000 0.0000;
6 24.0000 2.2000 0.0000;
7 24.0000 0.0000 0.0000;
MEMBER INCIDENCES
1 1 2;
2 2 3;
3 3 4;
4 4 5;
5 7 6;
6 6 5;
MEMBER PROPERTY INDIAN
1 2 5 6 TAPERED 0.40 0.010 0.25 0.012 0.75 0.25 0.012
3 4 TAPERED 0.75 0.010 0.25 0.012 0.40 0.25 0.012
SUPPORTS
1 7 FIXED
PERFORM ANALYSIS
FINISH";

            var builder = new PortalGeometryBuilder();
            var config = new PortalConfiguration
            {
                ModelName = "2D to 3D Portal Bracing Test",
                GenerationMode = "convert2d",
                Template2DStdText = std2D,
                BaySpacingExpression = "4@6.0", // 4 bays: 0, 1, 2, 3. Length = 24m
                Bracing = new BracingConfiguration
                {
                    IncludeRoofBracing = true,
                    IncludeWallBracing = true,
                    MaxBraceLength = 12.5,
                    BracedBayIndices = new List<int> { 0, 3 },
                    EnablePortalBracing = true,
                    PortalBracingHeight = 4.5,
                    PortalLegOffset = 0.5,
                    PortalBracedBayIndices = new List<int> { 1 } // Portal in Bay 1 on both walls
                }
            };

            var model = builder.Build(config);
            string std = StaadStdWriter.GenerateStdText(model);

            // 1. Verify Portal Header Beams: 2 walls * 3 segments = 6 beams at Y = 4.5
            var portalBeams = model.Beams.Values.Where(b => b.Type == MemberType.PortalBeam).ToList();
            Assert.Equal(6, portalBeams.Count);
            foreach (var pb in portalBeams)
            {
                var nA = model.Nodes[pb.NodeA];
                var nB = model.Nodes[pb.NodeB];
                Assert.True(Math.Abs(nA.Y - 4.5) < 0.01 && Math.Abs(nB.Y - 4.5) < 0.01);
            }

            // 2. Verify Portal Legs: 2 walls * 2 legs = 4 legs connecting base to Y = 4.5
            var portalLegs = model.Beams.Values.Where(b => b.Type == MemberType.PortalKneeBrace).ToList();
            Assert.Equal(4, portalLegs.Count);
            foreach (var leg in portalLegs)
            {
                var nA = model.Nodes[leg.NodeA];
                var nB = model.Nodes[leg.NodeB];
                double minY = Math.Min(nA.Y, nB.Y);
                double maxY = Math.Max(nA.Y, nB.Y);
                Assert.True(Math.Abs(minY - 0.0) < 0.01);
                Assert.True(Math.Abs(maxY - 4.5) < 0.01);
                // Verify end release exists for knee brace
                Assert.Contains(leg.Id, model.EndReleaseMyMzBeamIds);
            }

            // 3. Verify upper cross-bracing in Bay 1 above portal beam (Y from 4.5 to 7.0)
            var upperBracesBay1 = model.Beams.Values.Where(b => b.Type == MemberType.WallBracing &&
                (Math.Abs(model.Nodes[b.NodeA].Y - 4.5) < 0.01 || Math.Abs(model.Nodes[b.NodeB].Y - 4.5) < 0.01)).ToList();
            Assert.Equal(4, upperBracesBay1.Count);

            // 4. Verify STAAD output contains portal groups and member properties
            Assert.Contains("_PORTALBEAMS", std);
            Assert.Contains("_PORTALLEGS", std);
            Assert.Contains("TABLE ST ISMB350", std);
            Assert.Contains("TABLE ST ISMC150", std);
        }

        [Fact]
        public void Test_PortalBracing_LeftAndRightWall_Independent()
        {
            var builder = new PortalGeometryBuilder();
            var config = new PortalConfiguration
            {
                ModelName = "Independent Left Right Portal Bracing",
                BaySpacingExpression = "3@6.0", // bays 0, 1, 2
                Bracing = new BracingConfiguration
                {
                    IncludeRoofBracing = false,
                    IncludeWallBracing = true,
                    MaxBraceLength = 12.5,
                    BracedBayIndices = new List<int> { 0, 1, 2 },
                    EnablePortalBracing = true,
                    PortalBracingHeight = 4.0,
                    PortalLegOffset = 0.5,
                    PortalOnLeftWall = true,
                    PortalOnRightWall = false, // Right wall disabled for portal bracing!
                    PortalLeftBayIndices = new List<int> { 1 },
                    PortalRightBayIndices = new List<int> { }
                }
            };

            var model = builder.Build(config);

            // Left wall (X = 0) should have portal beams
            var leftPortalBeams = model.Beams.Values.Where(b => b.Type == MemberType.PortalBeam &&
                Math.Abs(model.Nodes[b.NodeA].X - 0.0) < 0.05).ToList();
            Assert.Equal(3, leftPortalBeams.Count); // 3 segments for 1 portal bay

            // Right wall (X = 24) should have NO portal beams
            var rightPortalBeams = model.Beams.Values.Where(b => b.Type == MemberType.PortalBeam &&
                Math.Abs(model.Nodes[b.NodeA].X - 24.0) < 0.05).ToList();
            Assert.Empty(rightPortalBeams);

            // Right wall bay 1 should have standard full-height wall cross-bracing
            var rightWallBracesInBay1 = model.Beams.Values.Where(b => b.Type == MemberType.WallBracing &&
                Math.Abs(model.Nodes[b.NodeA].X - 24.0) < 0.05 &&
                Math.Min(model.Nodes[b.NodeA].Z, model.Nodes[b.NodeB].Z) >= 5.9 &&
                Math.Max(model.Nodes[b.NodeA].Z, model.Nodes[b.NodeB].Z) <= 12.1).ToList();
            Assert.NotEmpty(rightWallBracesInBay1);
        }

        [Fact]
        public void Test_ColumnOnlyBrokenWhenJackBeamIsConnected()
        {
            string std2D = @"STAAD SPACE
START JOB INFORMATION
ENGINEER DATE 28-Feb-25
END JOB INFORMATION
INPUT WIDTH 79
UNIT METER KN
JOINT COORDINATES
1 0.0000 0.0000 0.0000;
2 0.0000 7.0000 0.0000;
3 12.0000 0.0000 0.0000;
4 12.0000 8.2000 0.0000;
5 24.0000 7.0000 0.0000;
6 24.0000 0.0000 0.0000;
MEMBER INCIDENCES
1 1 2;
2 2 4;
3 4 5;
4 6 5;
5 3 4;
MEMBER PROPERTY INDIAN
1 4 TAPERED 0.40 0.010 0.25 0.012 0.75 0.25 0.012
2 3 TAPERED 0.75 0.010 0.25 0.012 0.40 0.25 0.012
5 TABLE ST ISMB400
SUPPORTS
1 6 FIXED
3 PINNED
PERFORM ANALYSIS
FINISH";

            var builder = new PortalGeometryBuilder();

            // Scenario: Jack Portal enabled, but bay 0 (Z = 18 to 12) has a column at both ends (no skipped frames)
            // Intermediate columns at Z = 18 and Z = 12.
            // Bay 0 (Z=18 to 12) has NO jack beam.
            // Bay 1 (Z=12 to 0) has a skipped frame at Z = 6, so a Jack Beam exists between 12 and 0.
            var config = new PortalConfiguration
            {
                ModelName = "Jack Beam Break Test",
                GenerationMode = "convert2d",
                Template2DStdText = std2D,
                BaySpacingExpression = "1@6.0 + 2@6.0", // Z = 18, 12, 6, 0
                GableWalls = new GableWallConfiguration
                {
                    GableBaySpacingFront = "1@6+1@12+1@6", // Gable post at X = 6
                    GableBaySpacingRear = "1@6+1@12+1@6"
                },
                JackPortal = new JackPortalConfiguration
                {
                    Enabled = true,
                    IntermediateBaySpacingExpression = "1@6.0 + 1@12.0" // Full columns at Z = 18, 12, 0. Z = 6 skipped!
                }
            };

            var model = builder.Build(config);

            // 1. At Front Gable (Z = 18), Bay 0 (18 to 12) has NO skipped frame, so NO Jack Beam connects to Z = 18.
            // The gable post at X = 6 on Z = 18 has NO Jack Beam -> MUST NOT be broken at Y = 7.2m (1m below rafter).
            var gableNodesAt18 = model.Nodes.Values.Where(n => Math.Abs(n.X - 6.0) < 0.05 && Math.Abs(n.Z - 18.0) < 0.05).ToList();
            // Should only have base (Y=0) and rafter (Y=7.6m), NO node at Y ~ 6.6m!
            Assert.DoesNotContain(gableNodesAt18, n => Math.Abs(n.Y - 6.6) < 0.1);

            // The gable post member at X = 6, Z = 18 should be a single member connecting base to rafter
            var gableBeamsAt18 = model.Beams.Values.Where(b => b.Type == MemberType.GablePost &&
                Math.Abs(model.Nodes[b.NodeA].X - 6.0) < 0.05 && Math.Abs(model.Nodes[b.NodeB].X - 6.0) < 0.05 &&
                Math.Abs(model.Nodes[b.NodeA].Z - 18.0) < 0.05).ToList();
            Assert.Single(gableBeamsAt18);

            // 2. Gable post at X = 18 on Z = 18 also has NO Jack Beam -> MUST NOT be broken!
            var gableBeamsX18At18 = model.Beams.Values.Where(b => b.Type == MemberType.GablePost &&
                Math.Abs(model.Nodes[b.NodeA].X - 18.0) < 0.05 && Math.Abs(model.Nodes[b.NodeB].X - 18.0) < 0.05 &&
                Math.Abs(model.Nodes[b.NodeA].Z - 18.0) < 0.05).ToList();
            Assert.Single(gableBeamsX18At18);

            // 3. At Z = 12, a Jack Beam DOES connect (spanning from Z = 12 across Z = 6 to Z = 0)!
            // Therefore, the column at X = 12, Z = 12 MUST be broken at Y = 7.2m to connect the Jack Beam!
            var intColNodesAt12 = model.Nodes.Values.Where(n => Math.Abs(n.X - 12.0) < 0.05 && Math.Abs(n.Z - 12.0) < 0.05).ToList();
            Assert.Contains(intColNodesAt12, n => Math.Abs(n.Y - 7.2) < 0.1);

            var intColBeamsAt12 = model.Beams.Values.Where(b => b.Type == MemberType.IntermediateColumn &&
                Math.Abs(model.Nodes[b.NodeA].X - 12.0) < 0.05 && Math.Abs(model.Nodes[b.NodeB].X - 12.0) < 0.05 &&
                Math.Abs(model.Nodes[b.NodeA].Z - 12.0) < 0.05).ToList();
            Assert.Equal(2, intColBeamsAt12.Count); // Broken into 2 segments!

            // 4. When Jack Portal is disabled completely, intermediate column at Z = 12 MUST NOT be broken
            var configNoJack = new PortalConfiguration
            {
                ModelName = "No Jack Test",
                GenerationMode = "convert2d",
                Template2DStdText = std2D,
                BaySpacingExpression = "1@6.0 + 2@6.0",
                JackPortal = new JackPortalConfiguration { Enabled = false }
            };
            var modelNoJack = builder.Build(configNoJack);
            var intColNodesNoJack = modelNoJack.Nodes.Values.Where(n => Math.Abs(n.X - 12.0) < 0.05 && Math.Abs(n.Z - 12.0) < 0.05).ToList();
            Assert.DoesNotContain(intColNodesNoJack, n => Math.Abs(n.Y - 7.2) < 0.1);

            var intColBeamsNoJack = modelNoJack.Beams.Values.Where(b => b.Type == MemberType.IntermediateColumn &&
                Math.Abs(modelNoJack.Nodes[b.NodeA].X - 12.0) < 0.05 && Math.Abs(modelNoJack.Nodes[b.NodeB].X - 12.0) < 0.05 &&
                Math.Abs(modelNoJack.Nodes[b.NodeA].Z - 12.0) < 0.05).ToList();
            Assert.Single(intColBeamsNoJack); // Single continuous member!
        }

        [Fact]
        public void Test_Parametric_ColumnOnlyBrokenWhenJackBeamIsConnected()
        {
            var builder = new PortalGeometryBuilder();

            // Parametric Portal Modeler configuration
            // 24m width, 7m eave, 8.2m ridge -> at X = 12 (ridge), top rafter Y = 8.2m.
            // Tie Y for Jack Beam = 8.2 - 1.0 = 7.2m.
            var config = new PortalConfiguration
            {
                ModelName = "Parametric Jack Beam Break Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 8.2) },
                RccWallHeight = 0.0,
                WidthModuleExpression = "2@12", // Intermediate column line at X = 12
                BaySpacingExpression = "1@6.0 + 2@6.0", // Frames at Z = 18, 12, 6, 0
                GableWalls = new GableWallConfiguration
                {
                    GableBaySpacingFront = "1@6+1@12+1@6", // Gable posts at X = 6 and X = 18
                    GableBaySpacingRear = "1@6+1@12+1@6"
                },
                JackPortal = new JackPortalConfiguration
                {
                    Enabled = true,
                    IntermediateBaySpacingExpression = "1@6.0 + 1@12.0" // Columns at Z = 18, 12, 0. Z = 6 skipped!
                }
            };

            var model = builder.Build(config);

            // 1. At Front Gable (Z = 18), Gable post at X = 6 has NO Jack Beam connected.
            // MUST NOT have node inserted at Y = topY - 1.0 (~ 6.6m) and MUST NOT be broken!
            var gableNodesAt18 = model.Nodes.Values.Where(n => Math.Abs(n.X - 6.0) < 0.05 && Math.Abs(n.Z - 18.0) < 0.05).ToList();
            Assert.DoesNotContain(gableNodesAt18, n => Math.Abs(n.Y - 6.6) < 0.1);

            var gableBeamsAt18 = model.Beams.Values.Where(b => b.Type == MemberType.GablePost &&
                Math.Abs(model.Nodes[b.NodeA].X - 6.0) < 0.05 && Math.Abs(model.Nodes[b.NodeB].X - 6.0) < 0.05 &&
                Math.Abs(model.Nodes[b.NodeA].Z - 18.0) < 0.05).ToList();
            Assert.Single(gableBeamsAt18);

            // 2. Intermediate column at X = 12, Z = 12 DOES connect to a Jack Beam (spanning Z = 12 to 0).
            // Therefore, it MUST have a node inserted at Y = topY - 1.0m and MUST be broken into 2 segments!
            var intColNodesAt12 = model.Nodes.Values.Where(n => Math.Abs(n.X - 12.0) < 0.05 && Math.Abs(n.Z - 12.0) < 0.05).ToList();
            var topModNode = intColNodesAt12.OrderByDescending(n => n.Y).First();
            double expectedTieY = Math.Round(topModNode.Y - 1.0, 4);
            Assert.Contains(intColNodesAt12, n => Math.Abs(n.Y - expectedTieY) < 0.1);

            var intColBeamsAt12 = model.Beams.Values.Where(b => b.Type == MemberType.IntermediateColumn &&
                Math.Abs(model.Nodes[b.NodeA].X - 12.0) < 0.05 && Math.Abs(model.Nodes[b.NodeB].X - 12.0) < 0.05 &&
                Math.Abs(model.Nodes[b.NodeA].Z - 12.0) < 0.05).ToList();
            Assert.Equal(2, intColBeamsAt12.Count);

            // 3. When Jack Portal is disabled, intermediate column at Z = 12 MUST NOT be broken and NO node at Y = expectedTieY
            var configNoJack = new PortalConfiguration
            {
                ModelName = "Parametric No Jack Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 8.2) },
                RccWallHeight = 0.0,
                WidthModuleExpression = "2@12",
                BaySpacingExpression = "1@6.0 + 2@6.0",
                JackPortal = new JackPortalConfiguration { Enabled = false }
            };
            var modelNoJack = builder.Build(configNoJack);

            var intColNodesNoJack = modelNoJack.Nodes.Values.Where(n => Math.Abs(n.X - 12.0) < 0.05 && Math.Abs(n.Z - 12.0) < 0.05).ToList();
            Assert.DoesNotContain(intColNodesNoJack, n => Math.Abs(n.Y - expectedTieY) < 0.1);

            var intColBeamsNoJack = modelNoJack.Beams.Values.Where(b => b.Type == MemberType.IntermediateColumn &&
                Math.Abs(modelNoJack.Nodes[b.NodeA].X - 12.0) < 0.05 && Math.Abs(modelNoJack.Nodes[b.NodeB].X - 12.0) < 0.05 &&
                Math.Abs(modelNoJack.Nodes[b.NodeA].Z - 12.0) < 0.05).ToList();
            Assert.Single(intColBeamsNoJack);
        }

        [Fact]
        public void Test_FourSideCanopy_GenerationAndBayFiltering()
        {
            var builder = new PortalGeometryBuilder();

            // 24m span, 3 bays of 6m each (Z = 18, 12, 6, 0).
            // Bays: Bay 0 (Z: 18 to 12), Bay 1 (Z: 12 to 6), Bay 2 (Z: 6 to 0).
            // Gable posts at Front and Rear at X = 8, 16. (3 transverse bays: 0-8, 8-16, 16-24).
            var config = new PortalConfiguration
            {
                ModelName = "4-Side Canopy Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 8.2) },
                BaySpacingExpression = "3@6.0",
                GableWalls = new GableWallConfiguration
                {
                    GableBaySpacingFront = "8.0, 8.0",
                    GableBaySpacingRear = "8.0, 8.0"
                },
                Canopy = new CanopyConfiguration
                {
                    LeftWall = new CanopySideConfiguration
                    {
                        Enabled = true,
                        Height = 5.0,
                        Projection = 4.0,
                        Drop = 0.2,
                        BayExpression = "0, 1" // Only bays 0 and 1! Frame 0, 1, 2 have rafters. Frame 3 (Z=0) does not!
                    },
                    RightWall = new CanopySideConfiguration
                    {
                        Enabled = true,
                        Height = 5.0,
                        Projection = 3.5,
                        Drop = 0.2,
                        BayExpression = "2" // Only bay 2! Frame 2, 3 have rafters. Frame 0, 1 do not!
                    },
                    FrontWall = new CanopySideConfiguration
                    {
                        Enabled = true,
                        Height = 4.5,
                        Projection = 3.0,
                        Drop = 0.2,
                        BayExpression = "0" // Only transverse bay 0 (X: 0 to 8)!
                    },
                    RearWall = new CanopySideConfiguration
                    {
                        Enabled = true,
                        Height = 4.5,
                        Projection = 3.0,
                        Drop = 0.2,
                        BayExpression = "" // All transverse bays!
                    }
                }
            };

            var model = builder.Build(config);

            // 1. Verify Canopy Members Exist
            var canopyRafters = model.Beams.Values.Where(b => b.Type == MemberType.CanopyRafter).ToList();
            var canopyRunners = model.Beams.Values.Where(b => b.Type == MemberType.CanopyRunner).ToList();
            var canopyBracings = model.Beams.Values.Where(b => b.Type == MemberType.CanopyBracing).ToList();

            Assert.NotEmpty(canopyRafters);
            Assert.NotEmpty(canopyRunners);
            Assert.NotEmpty(canopyBracings);

            // 2. Left Wall Canopies (X = 0, projecting to X = -4.0):
            // Active bays: 0 (Z: 18-12) and 1 (Z: 12-6).
            // Rafters at frames 0 (Z=18), 1 (Z=12), 2 (Z=6).
            // NO rafter at frame 3 (Z=0).
            var leftRafters = canopyRafters.Where(b =>
                (model.Nodes[b.NodeA].X < -0.1 || model.Nodes[b.NodeB].X < -0.1)).ToList();
            Assert.Equal(3, leftRafters.Count);
            Assert.Contains(leftRafters, b => Math.Abs(model.Nodes[b.NodeA].Z - 18.0) < 0.1 || Math.Abs(model.Nodes[b.NodeB].Z - 18.0) < 0.1);
            Assert.Contains(leftRafters, b => Math.Abs(model.Nodes[b.NodeA].Z - 12.0) < 0.1 || Math.Abs(model.Nodes[b.NodeB].Z - 12.0) < 0.1);
            Assert.Contains(leftRafters, b => Math.Abs(model.Nodes[b.NodeA].Z - 6.0) < 0.1 || Math.Abs(model.Nodes[b.NodeB].Z - 6.0) < 0.1);
            Assert.DoesNotContain(leftRafters, b => Math.Abs(model.Nodes[b.NodeA].Z - 0.0) < 0.1 && Math.Abs(model.Nodes[b.NodeB].Z - 0.0) < 0.1);

            // 3. Right Wall Canopies (X = 24.0, projecting to X = 27.5):
            // Active bay: 2 (Z: 6 to 0).
            // Rafters at frames 2 (Z=6) and 3 (Z=0).
            // NO rafter at frame 0 (Z=18) or 1 (Z=12).
            var rightRafters = canopyRafters.Where(b =>
                (model.Nodes[b.NodeA].X > 24.1 || model.Nodes[b.NodeB].X > 24.1)).ToList();
            Assert.Equal(2, rightRafters.Count);
            Assert.Contains(rightRafters, b => Math.Abs(model.Nodes[b.NodeA].Z - 6.0) < 0.1 || Math.Abs(model.Nodes[b.NodeB].Z - 6.0) < 0.1);
            Assert.Contains(rightRafters, b => Math.Abs(model.Nodes[b.NodeA].Z - 0.0) < 0.1 || Math.Abs(model.Nodes[b.NodeB].Z - 0.0) < 0.1);
            Assert.DoesNotContain(rightRafters, b => Math.Abs(model.Nodes[b.NodeA].Z - 18.0) < 0.1);

            // 4. Front Wall Canopies (Z = 18.0, projecting to Z = 21.0):
            // Transverse posts at X = 0, 8, 16, 24. Bay 0 is X: 0 to 8.
            // Rafters at post 0 (X=0) and post 1 (X=8).
            var frontRafters = canopyRafters.Where(b =>
                (model.Nodes[b.NodeA].Z > 18.1 || model.Nodes[b.NodeB].Z > 18.1)).ToList();
            Assert.Equal(2, frontRafters.Count);
            Assert.Contains(frontRafters, b => Math.Abs(model.Nodes[b.NodeA].X - 0.0) < 0.1 || Math.Abs(model.Nodes[b.NodeB].X - 0.0) < 0.1);
            Assert.Contains(frontRafters, b => Math.Abs(model.Nodes[b.NodeA].X - 8.0) < 0.1 || Math.Abs(model.Nodes[b.NodeB].X - 8.0) < 0.1);

            // 5. Rear Wall Canopies (Z = 0.0, projecting to Z = -3.0):
            // All transverse bays enabled: rafters at X = 0, 8, 16, 24. (4 rafters).
            var rearRafters = canopyRafters.Where(b =>
                (model.Nodes[b.NodeA].Z < -0.1 || model.Nodes[b.NodeB].Z < -0.1)).ToList();
            Assert.Equal(4, rearRafters.Count);

            // 6. Verify STAAD writer writes _CANOPY group
            var stdContent = StaadPortalEngine.Exporters.StaadStdWriter.GenerateStdText(model);
            Assert.Contains("START GROUP DEFINITION", stdContent);
            Assert.Contains("_CANOPY", stdContent);
            Assert.Contains("TABLE ST ISMB200", stdContent);
            Assert.Contains("TABLE ST ISMC125", stdContent);
            Assert.Contains("TABLE ST ISA50X50X6", stdContent);
        }

        [Fact]
        public void Test_BreakMembersAtJoints_InterpolatesTaperedSections()
        {
            // Verify that members passing through joint locations are broken into sub-members,
            // and if a tapered section property was assigned, it is interpolated accurately at the joint
            // ensuring depth continuity across connected sub-members.
            string std2D = @"STAAD SPACE
START JOB INFORMATION
ENGINEER DATE 06-Sep-26
JOB NAME 2D Tapered Frame
END JOB INFORMATION
INPUT WIDTH 79
UNIT METER KN
JOINT COORDINATES
1 0.0000 0.0000 0.0000;
2 0.0000 6.0000 0.0000;
3 12.0000 7.2000 0.0000;
4 24.0000 6.0000 0.0000;
5 24.0000 0.0000 0.0000;
MEMBER INCIDENCES
1 1 2;
2 2 3;
3 3 4;
4 5 4;
MEMBER PROPERTY INDIAN
1 4 TAPERED 0.40 0.008 0.20 0.012 0.75 0.20 0.012
2 TAPERED 0.75 0.008 0.20 0.012 0.40 0.20 0.012
3 TAPERED 0.40 0.008 0.20 0.012 0.75 0.20 0.012
SUPPORTS
1 5 FIXED
PERFORM ANALYSIS
FINISH";

            var builder = new PortalGeometryBuilder();
            var config = new PortalConfiguration
            {
                ModelName = "Tapered Joint Break Model",
                GenerationMode = "convert2d",
                Template2DStdText = std2D,
                BaySpacingExpression = "1@6.0", // 1 bay = 2 frames at Z = 6.0 and Z = 0.0
                GableWalls = new GableWallConfiguration
                {
                    GableBaySpacingFront = "6.0", // Gable posts at X = 6, 12, 18
                    GableBaySpacingRear = "6.0"
                }
            };

            var model = builder.Build(config);
            Assert.NotNull(model);

            // Gable posts were inserted at X = 6.0 and X = 18.0.
            // Therefore, the rafter between X=0 and X=12 must be broken at X=6.0!
            var frontRaftersLeft = model.Beams.Values
                .Where(b => b.Type == MemberType.Rafter &&
                            Math.Abs(model.Nodes[b.NodeA].Z - 6.0) < 0.1 &&
                            model.Nodes[b.NodeA].X <= 12.0 && model.Nodes[b.NodeB].X <= 12.0)
                .OrderBy(b => Math.Min(model.Nodes[b.NodeA].X, model.Nodes[b.NodeB].X))
                .ToList();

            // Must have 2 segments: (0 -> 6) and (6 -> 12)
            Assert.Equal(2, frontRaftersLeft.Count);

            var seg1 = frontRaftersLeft[0]; // 0 to 6
            var seg2 = frontRaftersLeft[1]; // 6 to 12

            Assert.Contains("TAPERED", seg1.SectionProperty);
            Assert.Contains("TAPERED", seg2.SectionProperty);

            // Parse depths:
            // Original: start depth = 0.75, end depth = 0.40
            // At X=6 (t=0.5): depth should be 0.75 + 0.5*(0.40 - 0.75) = 0.575
            // So seg1: start=0.75, end=0.575
            // seg2: start=0.575, end=0.40
            var seg1Parts = seg1.SectionProperty.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var seg2Parts = seg2.SectionProperty.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            double seg1DStart = double.Parse(seg1Parts[1], System.Globalization.CultureInfo.InvariantCulture);
            double seg1DEnd = double.Parse(seg1Parts[5], System.Globalization.CultureInfo.InvariantCulture);

            double seg2DStart = double.Parse(seg2Parts[1], System.Globalization.CultureInfo.InvariantCulture);
            double seg2DEnd = double.Parse(seg2Parts[5], System.Globalization.CultureInfo.InvariantCulture);

            Assert.Equal(0.75, seg1DStart, 3);
            Assert.Equal(0.575, seg1DEnd, 3);
            Assert.Equal(0.575, seg2DStart, 3);
            Assert.Equal(0.40, seg2DEnd, 3);

            // Verify continuous junction depth
            Assert.Equal(seg1DEnd, seg2DStart, 3);

            // Verify STAAD .std output contains both interpolated sections
            string std = StaadPortalEngine.Exporters.StaadStdWriter.GenerateStdText(model);
            Assert.Contains("TAPERED 0.75", std);
            Assert.Contains("0.575", std);
        }
    }
}

