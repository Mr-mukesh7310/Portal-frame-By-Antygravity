using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using StaadPortalEngine.Connectors;
using StaadPortalEngine.Exporters;
using StaadPortalEngine.Generators;
using StaadPortalEngine.Models;
using StaadPortalEngine.Parsers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/generate", (PortalConfiguration config) =>
{
    var geometryBuilder = new PortalGeometryBuilder();
    var model = geometryBuilder.Build(config);
    var stdText = StaadStdWriter.GenerateStdText(model);

    var baseNodesList = model.Nodes.Values.Where(n => n.Y <= 0.25 || model.BaseNodeIds.Contains(n.Id)).ToList();
    double totalW = baseNodesList.Count > 0 ? (baseNodesList.Max(n => n.X) - baseNodesList.Min(n => n.X)) : (model.Nodes.Count > 0 ? (model.Nodes.Values.Max(n => n.X) - model.Nodes.Values.Min(n => n.X)) : config.TotalWidth);
    double totalL = model.Nodes.Count > 0 ? (model.Nodes.Values.Max(n => n.Z) - model.Nodes.Values.Min(n => n.Z)) : config.TotalLength;
    double maxH = model.Nodes.Count > 0 ? model.Nodes.Values.Max(n => n.Y) : (config.Spans.Count > 0 ? config.Spans[0].RidgeHeight : 7.0);

    var response = new
    {
        nodes = model.Nodes.Values,
        beams = model.Beams.Values,
        baseNodes = model.BaseNodeIds,
        fixedBaseNodes = model.FixedBaseNodeIds,
        pinnedBaseNodes = model.PinnedBaseNodeIds,
        startReleaseBeams = model.StartReleaseMyMzBeamIds,
        endReleaseBeams = model.EndReleaseMyMzBeamIds,
        stdText = stdText,
        stats = new
        {
            nodeCount = model.Nodes.Count,
            beamCount = model.Beams.Count,
            totalWidth = Math.Round(totalW, 2),
            totalLength = Math.Round(totalL, 2),
            maxHeight = Math.Round(maxH, 2),
            baseSupports = model.BaseNodeIds.Count,
            fixedSupports = model.FixedBaseNodeIds.Count,
            pinnedSupports = model.PinnedBaseNodeIds.Count,
            tensionMembers = model.TensionOnlyBeamIds.Count,
            pinnedMembers = model.PinnedEndBeamIds.Count
        }
    };

    return Results.Ok(response);
});

// API: Parse 2D STAAD Model
app.MapPost("/api/parse-2d", (Parse2DRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request?.StdText))
    {
        return Results.BadRequest(new { error = "STAAD script text is empty." });
    }

    try
    {
        var model = StaadStdParser.Parse(request.StdText);
        var parameters = StaadStdParser.ExtractParameters(model);

        var sectionProps = model.Beams.Values
            .Where(b => !string.IsNullOrWhiteSpace(b.SectionProperty))
            .Select(b => b.SectionProperty)
            .Distinct()
            .ToList();

        var response = new
        {
            parameters = parameters,
            nodeCount = model.Nodes.Count,
            beamCount = model.Beams.Count,
            spanWidth = parameters.SpanWidth,
            eaveHeight = parameters.EaveHeight,
            ridgeHeight = parameters.RidgeHeight,
            roofSlopeRatio = parameters.RoofSlopeRatio,
            rccWallHeight = parameters.RccWallHeight,
            hasLeftCanopy = parameters.HasLeftCanopy,
            leftCanopyHeight = parameters.LeftCanopyHeight,
            hasRightCanopy = parameters.HasRightCanopy,
            rightCanopyHeight = parameters.RightCanopyHeight,
            intermediateColumnCount = parameters.IntermediateColumnOffsets.Count,
            widthModuleExpression = parameters.WidthModuleExpression,
            detectedSections = sectionProps,
            isTapered = sectionProps.Any(p => p.ToUpperInvariant().Contains("TAPERED")),
            nodes = model.Nodes.Values,
            beams = model.Beams.Values
        };

        return Results.Ok(response);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Failed to parse 2D STAAD file: {ex.Message}" });
    }
});

// API 2: Export .std file
app.MapPost("/api/export-std", (PortalConfiguration config) =>
{
    var geometryBuilder = new PortalGeometryBuilder();
    var model = geometryBuilder.Build(config);
    var stdText = StaadStdWriter.GenerateStdText(model);

    var bytes = Encoding.UTF8.GetBytes(stdText);
    var filename = $"STAAD_{config.ModelName.Replace(" ", "_")}_{DateTime.Now:yyyyMMdd_HHmmss}.std";

    return Results.File(bytes, "text/plain", filename);
});

// API 3: Push to Live STAAD.Pro instance via OpenSTAAD COM
app.MapPost("/api/push-openstaad", (PortalConfiguration config) =>
{
    if (!OperatingSystem.IsWindows())
    {
        return Results.Ok(new OpenStaadResult
        {
            Success = false,
            Message = "OpenSTAAD COM automation is only available on Windows operating system."
        });
    }

    var geometryBuilder = new PortalGeometryBuilder();
    var model = geometryBuilder.Build(config);
    var result = OpenStaadConnector.PushToActiveStaad(model);
    return Results.Ok(result);
});

// API 4: Presets library
app.MapGet("/api/presets", () =>
{
    var presets = new Dictionary<string, PortalConfiguration>
    {
        ["SingleGableWarehouse"] = new PortalConfiguration
        {
            ModelName = "Parametric Industrial Shed (50m x 83m)",
            FrameType = FrameType.SingleGable,
            Spans = new List<SpanDefinition> { new SpanDefinition(50.0, 15.0, 5.0) },
            WidthModuleExpression = "3",
            BaySpacingExpression = "1@6.5+10@7+1@6.5",
            BaySpacings = new List<double> { 6.5, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 6.5 },
            RccWallHeight = 3.0,
            GableWalls = new GableWallConfiguration { GableBaySpacingFront = "3", GableBaySpacingRear = "3" },
            JackPortal = new JackPortalConfiguration { Enabled = true, IntermediateBaySpacingExpression = "1@6.5+5@14+1@6.5" },
            CableTray = new CableTrayConfiguration { Enabled = false, BracketHeight = 5.0, BracketLength = 0.8 },
            Bracing = new BracingConfiguration { IncludeRoofBracing = true, IncludeWallBracing = true, BracedBayIndices = new List<int> { 0, 5, 11 } }
        },
        ["TwinSpanIndustrialShed"] = new PortalConfiguration
        {
            ModelName = "Twin Span Multi-Gable Industrial Shed",
            FrameType = FrameType.MultiSpanGable,
            Spans = new List<SpanDefinition>
            {
                new SpanDefinition(18.0, 6.5, 10.0),
                new SpanDefinition(18.0, 6.5, 10.0)
            },
            BaySpacingExpression = "4@7.0",
            BaySpacings = new List<double> { 7.0, 7.0, 7.0, 7.0 },
            RccWallHeight = 2.2,
            GableWalls = new GableWallConfiguration { GableBaySpacingFront = "6@6", GableBaySpacingRear = "6@6" },
            Crane = new CraneConfiguration { Enabled = true, BracketHeight = 4.8, BracketLength = 0.75, OnInteriorColumns = true },
            Bracing = new BracingConfiguration { IncludeRoofBracing = true, IncludeWallBracing = true, BracedBayIndices = new List<int> { 0, 3 } }
        },
        ["FactoryWithMezzanine"] = new PortalConfiguration
        {
            ModelName = "Heavy Factory with Mezzanine & Crane",
            FrameType = FrameType.SingleGable,
            Spans = new List<SpanDefinition> { new SpanDefinition(30.0, 8.0, 10.0) },
            BaySpacingExpression = "6@6.0",
            BaySpacings = new List<double> { 6.0, 6.0, 6.0, 6.0, 6.0, 6.0 },
            RccWallHeight = 2.5,
            GableWalls = new GableWallConfiguration { GableBaySpacingFront = "5@6", GableBaySpacingRear = "5@6" },
            Crane = new CraneConfiguration { Enabled = true, BracketHeight = 5.8, BracketLength = 0.9 },
            Mezzanine = new MezzanineConfiguration
            {
                Enabled = true,
                FloorHeight = 3.8,
                StartX = 0.0,
                EndX = 12.0,
                BayIndices = new List<int> { 0, 1 },
                JoistSpacing = 1.5
            },
            Bracing = new BracingConfiguration { IncludeRoofBracing = true, IncludeWallBracing = true, BracedBayIndices = new List<int> { 0, 5 } }
        }
    };

    return Results.Ok(presets);
});

app.Run();

public record Parse2DRequest(string StdText);
