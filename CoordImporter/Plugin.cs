using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.Text.RegularExpressions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Lumina.Excel.GeneratedSheets;
using CoordImporter.Windows;
using Dalamud;

namespace CoordImporter;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "Coordinate Importer";
    private const string CommandName = "/ci";

    private DalamudPluginInterface PluginInterface { get; init; }
    private ICommandManager CommandManager { get; init; }
    private WindowSystem WindowSystem = new("CoordinateImporter");
    private IChatGui Chat { get; }
    private IDataManager DataManager { get; }
    private IPluginLog Logger { get; }
    private IDictionary<string, Map> Maps { get; } = new Dictionary<string, Map>();

    private MainWindow MainWindow { get; init; }

    public Plugin(
        [RequiredVersion("1.0")] DalamudPluginInterface pluginInterface,
        [RequiredVersion("1.0")] ICommandManager commandManager,
        IChatGui chat,
        IDataManager dataManager,
        IPluginLog logger)
    {
        PluginInterface = pluginInterface;
        CommandManager = commandManager;
        Chat = chat;
        DataManager = dataManager;
        Logger = logger;

        // This should support names of maps in all available languages
        // We create a hashtable where the key is the name of the map (in whatever language) and the value is the map object
        foreach (ClientLanguage cl in ClientLanguage.GetValuesAsUnderlyingType<ClientLanguage>())
        {
            if (DataManager.GetExcelSheet<Map>(cl) != null)
            {
                for (uint i = 0; i < DataManager.GetExcelSheet<Map>(cl)!.RowCount; i++)
                {
                    var placeNameSheet = DataManager.GetExcelSheet<PlaceName>(cl);
                    if (placeNameSheet == null) continue;
                    var map = DataManager.GetExcelSheet<Map>(cl)!.GetRow(i);
                    if (map == null) continue;
                    var placeName = placeNameSheet.GetRow(map.PlaceName.Row);
                    if (placeName == null) continue;
                    Logger.Verbose($"Adding map with name {placeName.Name} with language {cl}");
                    if (!Maps.TryAdd(placeName.Name, map))
                    {
                        Logger.Verbose($"Attempted to add map with name {placeName.Name} for language {cl} but it already existed");
                    }
                }
            }

            Logger.Debug($"Loaded Map data from ClientLanguage {cl}");
        }

        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Paste coordinates in dialog box and click 'Import'. Coordinates will show in echo chat."
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
    }

    public void Dispose()
    {
        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        // in response to the slash command, just display our main ui
        MainWindow.IsOpen = true;
    }

    private void DrawUI()
    {
        WindowSystem.Draw();
    }

    private static readonly Dictionary<string, string> InstanceKeyMap = new()
    {
        { "1", "\ue0b1" },
        { "2", "\ue0b2" },
        { "3", "\ue0b3" },
    };

    // For the format "(Maybe: Storsie) \ue0bbLabyrinthos ( 17  , 9.6 ) " (including the icky unicode instance/arrow)
    private static readonly Regex SirenRegex = new(@"^\s*\(Maybe\:\s*(?<mark_name>[\w\s\'\-]+)\)\s+\ue0bb(?<map_name>[\w\s\'\-]+)(?<instance_number>\ue0b1|\ue0b2|\ue0b3)?\s\(\s*(?<x_coord>[\d\.]+)\s*,\s*(?<y_coord>[\d\.]+)\s*\)");

    // For the format "Labyrinthos ( 16.5 , 16.8 ) Storsie"
    private static readonly Regex BearRegex = new(@"^\s*(?<map_name>[\w\s\'\-]+?)\s+(?<instance_number>[123])?\s*\(\s*(?<loc>(?<x_coord>[\d\.]+)\s*,\s*(?<y_coord>[\d\.]+)|NOT AVAILABLE)\s*\)\s*(?<mark_name>[\w\s\'\-]+)\s*$");

    // For the format "Raiden [S]: Gamma - Yanxia ( 23.6, 11.4 )"
    private static readonly Regex FaloopRegex = new(@"^\s*(?<world_name>\w+)\s+\[S\]: (?<mark_name>[\w\s'-]+)\s*-\s*(?<map_name>[\w\s'-]+)\s+\(?(?<instance_number>[123]?)\)?\s*\(\s*(?<x_coord>[\d\.]+)\s*,\s*(?<y_coord>[\d\.]+)\s*\)");

    public void EchoString(string pastedPayload)
    {
        string[] splitStrings = pastedPayload.Split(new[] { '\r', '\n' },
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (string inputLine in splitStrings)
        {
            Match match;
            // Check if the little arrow symbol is in the text. If it is then the line is from Siren
            if ((match = SirenRegex.Match(inputLine)).Success)
            {
                Logger.Debug($"Siren regex matched for input {inputLine}. Groups are {DumpGroups(match.Groups)}");
            }
            // If we get here then the string can be from Faloop or Bear. Easiest way to discern them is to
            // check if the string contains '[S]', which is unique to Faloop
            else if ((match = FaloopRegex.Match(inputLine)).Success)
            {
                Logger.Debug($"Faloop regex matched for input {inputLine}. Groups are {DumpGroups(match.Groups)}");
            }
            else if ((match = BearRegex.Match(inputLine)).Success)
            {
                // If a map on Bear doesn't have a mark's location then the coordinates are 'NOT AVAILABLE'
                if (match.Groups["loc"].Value == "NOT AVAILABLE")
                {
                    Logger.Debug($"Input {inputLine} does not have coordinates. Ignoring");
                    continue;
                }

                Logger.Debug($"Bear regex matched for input {inputLine}. Groups are {DumpGroups(match.Groups)}");
            }
            else
            {
                Logger.Error($"Unknown input string '{inputLine}'");
                continue;
            }

            // Faloop (and Bear) doesn't use the 1/2/3 instance symbols directly (while Siren does), so 
            // use this dictionary to get the symbol for the output
            string instanceId =
                InstanceKeyMap.TryGetValue(match.Groups["instance_number"].Value, out string? instanceNumber)
                    ? instanceNumber
                    : match.Groups["instance_number"].Value;

            string mapName = match.Groups["map_name"].Value.Trim();
            string markName = match.Groups["mark_name"].Value;
            float x = float.Parse(match.Groups["x_coord"].Value, CultureInfo.InvariantCulture);
            float y = float.Parse(match.Groups["y_coord"].Value, CultureInfo.InvariantCulture);

            var output = Maps.TryGetValue(mapName, out var map)
                ? CreateMapLink(map.TerritoryType.Value!.RowId, map.RowId, x, y, instanceId, markName)
                : $"Input text \"{inputLine}\" invalid. Could not find a matching map for {match.Groups["map_name"]}.";

            Chat.Print(new XivChatEntry
            {
                Type = XivChatType.Echo,
                Name = "",
                Message = output
            });
        }
    }

    // This is a custom version of Dalamud's CreateMapLink method. It includes the mark name and the instance ID
    private static SeString CreateMapLink(uint territoryId, uint mapId, float xCoord, float yCoord,
        string? instanceId, string markName)
    {
        var mapLinkPayload = new MapLinkPayload(territoryId, mapId, xCoord, yCoord);
        var text = $"{mapLinkPayload.PlaceName}{instanceId} {mapLinkPayload.CoordinateString} ({markName})";

        var payloads = new List<Payload>(new Payload[]
        {
            mapLinkPayload,
            new TextPayload(text),
            RawPayload.LinkTerminator
        });
        payloads.InsertRange(1, SeString.TextArrowPayloads);
        return new SeString(payloads);
    }

    private static string DumpGroups(GroupCollection groups)
    {
        return string.Join(',', groups.Values.Select(group => $"({group.Name}:{group.Value})"));
    }
}
