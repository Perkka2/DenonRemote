using System.Xml.Linq;
using DenonRemote.Models;
using Microsoft.Extensions.Logging.Abstractions;
using DenonRemote.Components;

namespace DenonRemote.Services;

/// <summary>
/// Checks over the parts that have actually had bugs: protocol parsing, command
/// encoding and the XML the two undocumented HTTP APIs speak.
///
/// Run with --self-test. It is a plain flag on the app rather than a test project
/// so it needs no packages and runs anywhere the app runs, CI included.
/// </summary>
public static class SelfTest
{
    private static int _passed;
    private static readonly List<string> Failures = [];

    public static int Run()
    {
        ProtocolParsing();
        ZoneParsing();
        ParameterParsing();
        SystemParsing();
        CommandEncoding();
        GraphicEqPayloads();
        SetupApiParsing();
        LabelTable();
        SetupUiCrawl();
        LegacySourceTable();
        NetworkPlayer();
        AspSetupPages();

        Console.WriteLine();
        Console.WriteLine($"  {_passed} passed, {Failures.Count} failed");
        foreach (var failure in Failures) Console.WriteLine("    FAIL  " + failure);
        Console.WriteLine();

        return Failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// The setup-UI capture follows the receiver's own links, so what it refuses to
    /// follow matters more than what it follows: this UI changes settings through
    /// ordinary-looking links.
    /// </summary>
    private static void SetupUiCrawl()
    {
        var origin = new Uri("http://10.0.1.197");

        Check("a submit page is never fetched",
            !HttpProbe.SafeToRead(new Uri("http://10.0.1.197/SETUP/AUDIO/GRAPHICEQ/s_audio.asp")));
        Check("a link carrying a value is never fetched",
            !HttpProbe.SafeToRead(new Uri("http://10.0.1.197/SETUP/AUDIO/d_audio.asp?ch=1&val=3")));
        Check("a plain page is fetched",
            HttpProbe.SafeToRead(new Uri("http://10.0.1.197/SETUP/AUDIO/VOLUME/f_audio.asp")));

        Check("another host is not crawled",
            !HttpProbe.SameOrigin(origin, new Uri("http://denon.com/x.asp")));
        Check("another port is not crawled",
            !HttpProbe.SameOrigin(origin, new Uri("http://10.0.1.197:10443/x.asp")));
        Check("the receiver itself is crawled",
            HttpProbe.SameOrigin(origin, new Uri("http://10.0.1.197/SETUP/f_home.asp")));

        // The zone and player pages build their script tag at runtime, so the file
        // holding all their behaviour is invisible to a tag-only scan.
        var zonePage = """
            <html><body><div id="contents"></div>
            <script type="text/javascript">
              function loadLib(lib){ var s = document.createElement("script"); s.src = lib; }
              setTimeout(function(){
                loadLib('/lib/jquery/jquery.pack.js');
                loadLib('index.js?201112230000');
              }, 500);
            </script></body></html>
            """;
        var found = HttpProbe.ReferencesOf(zonePage).ToList();
        Check("a runtime-loaded script is followed", found.Contains("index.js?201112230000"));
        Check("its library is followed too", found.Contains("/lib/jquery/jquery.pack.js"));

        // ...and a cache-buster on an asset must not be mistaken for a value.
        Check("a cache-busted script is fetched",
            HttpProbe.SafeToRead(new Uri("http://10.0.1.197/MainZone/index.js?201112230000")));

        Check("a fragment is not a separate page",
            new Uri(new Uri("http://h/NetAudio/index.html#").GetLeftPart(UriPartial.Query))
                == new Uri("http://h/NetAudio/index.html"));

        Check("a host with a port is not also guessed at 10443",
            HttpProbe.OriginCandidates("10.0.1.197:8099").SequenceEqual(new[] { "http://10.0.1.197:8099" }));
        Check("a bare host tries the setup API first",
            HttpProbe.OriginCandidates("10.0.1.197")[0] == "https://10.0.1.197:10443");

        // A mistyped flag used to fall through and start the web app instead, which
        // looks exactly like a capture that ran and wrote nothing.
        Check("a mistyped flag is caught",
            StartupOptions.Parse(["--prob", "10.0.1.197"]).Unknown.Contains("--prob"));
        Check("a real flag is not", StartupOptions.Parse(["--probe", "10.0.1.197"]).Unknown.Count == 0);
        Check("probe takes its host", StartupOptions.Parse(["--probe", "10.0.1.197"]).Probe == "10.0.1.197");
        Check("fetch-ui still works", StartupOptions.Parse(["--fetch-ui", "10.0.1.197"]).FetchUi == "10.0.1.197");
        // The host passes its own arguments through; only flags are ours to reject.
        Check("a bare argument is left alone", StartupOptions.Parse(["something"]).Unknown.Count == 0);

        Check("an asp page is walked", HttpProbe.IsDocument(new Uri("http://h/SETUP/f_home.asp")));
        Check("an html page is walked", HttpProbe.IsDocument(new Uri("http://h/audio/audio.html")));
        Check("a script is not walked", !HttpProbe.IsDocument(new Uri("http://h/jquery.js")));
    }

    /// <summary>
    /// The pre-HEOS units answer GetRenameSource with an empty document, but publish
    /// the same table in their zone status document as three arrays in step.
    /// </summary>
    private static void LegacySourceTable()
    {
        var zone = XDocument.Parse("""
            <item>
              <InputFuncList>
                <value>CBL/SAT</value><value>Blu-ray</value><value>NETWORK</value>
                <value>TV AUDIO</value><value>PHONO</value>
              </InputFuncList>
              <RenameSource>
                <value><value>Sky Box     </value></value>
                <value><value>Blu-ray     </value></value>
                <value><value>Online Music</value></value>
                <value><value>TV Audio    </value></value>
                <value><value>Phono       </value></value>
              </RenameSource>
              <SourceDelete>
                <value>USE</value><value>DELETE</value><value></value>
                <value>USE</value><value>USE</value>
              </SourceDelete>
            </item>
            """);

        var sources = DeviceProfileReader.ParseLegacySources(zone);

        Check("the renamed input keeps the user's name",
            sources.Any(s => s.Code == "SAT/CBL" && s.Label == "Sky Box"));
        Check("codes are normalised to the protocol's",
            sources.Any(s => s.Code == "NET") && sources.Any(s => s.Code == "TV"));
        Check("a deleted input is hidden", sources.All(s => s.Code != "BD"));

        // This firmware leaves the flag blank on at least one row - and on the very
        // input that was playing. Hiding what is playing is the worse failure.
        Check("a blank flag does not hide an input", sources.Any(s => s.Code == "NET"));
        Check("every other input survives", sources.Count == 4);

        // The arrays need not be the same length.
        var ragged = XDocument.Parse("""
            <item>
              <InputFuncList><value>CD</value><value>TUNER</value></InputFuncList>
              <RenameSource><value><value>Compact Disc</value></value></RenameSource>
              <SourceDelete><value>USE</value></SourceDelete>
            </item>
            """);
        var short_ = DeviceProfileReader.ParseLegacySources(ragged);
        Check("a short rename list is survivable", short_.Count == 2);
        Check("an unnamed input falls back to the catalog",
            short_.Any(s => s.Code == "TUNER" && s.Label.Length > 0));
    }

    /// <summary>
    /// The pre-HEOS network player. This is the document the receiver was actually
    /// serving when it was captured, Spotify playing.
    /// </summary>
    private static void NetworkPlayer()
    {
        var playing = XDocument.Parse("""
            <item>
              <chFlag><value>0</value><value>0</value><value>1</value><value>0</value></chFlag>
              <szLine>
                <value>Now Playing</value>
                <value>Out In The Night</value>
                <value>Arvid Nero</value>
                <value></value>
                <value>Little White Dove</value>
                <value></value><value></value><value></value><value></value><value></value>
              </szLine>
              <NetPlayingTitle><value>Spotify</value></NetPlayingTitle>
              <Art><value>2</value></Art>
              <NetAudioRandom><value>OFF</value></NetAudioRandom>
              <NetAudioRepeat><value>ON</value></NetAudioRepeat>
              <InputFuncSelect><value>Online Music</value></InputFuncSelect>
            </item>
            """);

        var state = NetAudioClient.Parse(playing)!;

        Check("the screen's heading is read", state.Heading == "Now Playing");
        Check("the service is read", state.Service == "Spotify");
        Check("the input is read", state.Input == "Online Music");
        Check("art is offered", state.HasArt);
        Check("repeat is read", state.Repeat);
        Check("shuffle is read", !state.Shuffle);

        // Ten slots, five used: the blank tail would otherwise draw as empty rows.
        Check("the blank tail is dropped", state.Lines.Count == 5);
        // ...but a blank between two used lines is part of the screen's shape.
        Check("a gap inside the screen is kept", state.Lines[3] == "");
        Check("the body skips blanks",
            state.Body.SequenceEqual(new[] { "Out In The Night", "Arvid Nero", "Little White Dove" }));
        Check("the cursor line is marked", state.LineFlags[2] == 1);
        Check("other lines are not", state.LineFlags[1] == 0);

        // An idle player says so with an empty buffer rather than by not answering.
        var idle = NetAudioClient.Parse(XDocument.Parse("""
            <item><szLine><value></value><value></value></szLine><Art><value>0</value></Art></item>
            """))!;
        Check("an empty screen is idle", idle.Idle);
        Check("no art is offered when there is none", !idle.HasArt);

        // The command the UI itself sends, field for field.
        var body = NetAudioClient.Body("CurDown");
        Check("a command names the player", body["cmd0"] == "PutNetAudioCommand/CurDown");
        Check("a command asks for a fresh status", body["cmd1"] == "aspMainZone_WebUpdateStatus/");
        Check("a command carries the zone", body["ZoneName"] == "MAIN ZONE");

        Check("the status document is where the UI reads it",
            NetAudioClient.StatusUrl("10.0.1.197")
                == "http://10.0.1.197/goform/formNetAudio_StatusXml.xml");
        Check("commands go where the UI posts them",
            NetAudioClient.CommandUrl("10.0.1.197") == "http://10.0.1.197/NetAudio/index.put.asp");
    }

    /// <summary>
    /// The pre-HEOS setup pages. These fragments are the real markup, verbatim -
    /// including the parts that caused each bug noted below.
    /// </summary>
    private static void AspSetupPages()
    {
        // Radios, with the checked one carrying the current value.
        var speakers = AspSetupClient.Parse("""
            <FORM name="spsetup" action="s_speakersetup.asp" method="POST">
            <INPUT type='hidden' name='setPureDirectOn' value='OFF'>
            <INPUT type='hidden' name='setSetupLock' value='OFF'>
            <div class="Title">Speakers/Speaker Config.</div>
            <div class="HelpText">Selects the use and size of each speaker</div>
            <TABLE>
            <TR><TD nowrap><B>&nbsp;&nbsp;Front</B></TD><TD>
              <INPUT type='radio' name='radioSpConfigFr' value='Large' onClick='radioBtn()'>Large<INPUT type='radio' name='radioSpConfigFr' value='Small' onClick='radioBtn()' checked>Small
            </TD></TR>
            <TR><TD nowrap><B>&nbsp;&nbsp;Subwoofer</B></TD><TD>
              <INPUT type='radio' name='radioSpConfigSw' value='1spkr' onClick='radioBtn()'>1 spkr<INPUT type='radio' name='radioSpConfigSw' value='2spkrs' onClick='radioBtn()' checked>2 spkrs<INPUT type='radio' name='radioSpConfigSw' value='None' onClick='radioBtn()'>None
            </TD></TR>
            </TABLE></FORM>
            """)!;

        Check("the page names itself", speakers.Title == "Speakers/Speaker Config.");
        Check("the page explains itself", speakers.Help.StartsWith("Selects the use"));
        Check("a radio group is one setting", speakers.Rows.Count == 2);
        Check("the checked radio is the value",
            speakers.Rows[0].Value == "Small" && speakers.Rows[0].Label == "Front");
        Check("its siblings are the choices",
            speakers.Rows[1].Options.Select(o => o.Text).SequenceEqual(new[] { "1 spkr", "2 spkrs", "None" }));
        Check("an unlocked page is unlocked", !speakers.Locked);

        // Pure Direct and Setup Lock are the receiver's own refusals.
        var locked = AspSetupClient.Parse("""
            <FORM action="s_speakersetup.asp"><INPUT type='hidden' name='setPureDirectOn' value='ON'>
            <TABLE><TR><TD><B>Front</B></TD><TD><INPUT type='radio' name='r' value='A' checked>A</TD></TR></TABLE></FORM>
            """)!;
        Check("Pure Direct locks the page", locked.LockedBy == "Pure Direct is on");
        Check("and every setting on it", locked.Rows.All(r => r.Locked));

        // A leading dash is indentation on a label - and a minus sign on a value.
        var volume = AspSetupClient.Parse("""
            <FORM action="s_audio.asp"><TABLE>
            <TR><TD><B> -Limit</B></TD><TD>
              <select name='listLimit'><OPTION value='OFF' selected>Off</OPTION><OPTION value='M20'>-20dB</OPTION></SELECT>
            </TD></TR></TABLE></FORM>
            """)!;
        Check("an indented label loses its dash", volume.Rows[0].Label == "Limit");
        Check("a negative value keeps its minus",
            volume.Rows[0].Options.Any(o => o.Text == "-20dB"));

        // A radio group beside its text box is one setting in two parts, not a table.
        var pair = AspSetupClient.Parse("""
            <FORM action="s_audio.asp"><TABLE>
            <TR><TD><B>Power On Level</B></TD><TD>
              <INPUT type='radio' name='radioPw' value='LAST' checked>Last<INPUT type='radio' name='radioPw' value='LVL'>Level
              <INPUT type='text' name='textPw' value='45'>
            </TD></TR></TABLE></FORM>
            """)!;
        Check("a setting and its box are not a table", pair.Rows.All(r => r.Index is null));
        Check("the setting is named once", pair.Rows.Count(r => r.Label == "Power On Level") == 1);

        // Input assign is a table, and says so with an empty corner cell.
        var assign = AspSetupClient.Parse("""
            <FORM action="s_InputAssign.asp"><TABLE>
            <tr><td></td><td><B>HDMI</B></td><td><B>DIGITAL</B></td></tr>
            <tr><TD><B>CBL/SAT</B></td>
              <TD><select name='listHdmiAssignSAT/CBL'><OPTION value='HD1' selected>1</OPTION><OPTION value='HD2'>2</OPTION></SELECT></td>
              <TD><select name='listDigitalAssignSAT/CBL'><OPTION value='CO1' selected>COAX1</OPTION><OPTION value='OFF'>-</OPTION></SELECT></td>
            </tr>
            <tr><TD><B>DVD</B></td>
              <TD><select name='listHdmiAssignDVD'><OPTION value='HD1'>1</OPTION><OPTION value='HD2' selected>2</OPTION></SELECT></td>
              <TD><select name='listDigitalAssignDVD'><OPTION value='CO1'>COAX1</OPTION><OPTION value='OFF' selected>-</OPTION></SELECT></td>
            </tr></TABLE></FORM>
            """)!;
        Check("the header names the columns",
            assign.Rows.Select(r => r.Name).Distinct().SequenceEqual(new[] { "HDMI", "DIGITAL" }));
        Check("the rows are the sources",
            assign.Rows.Select(r => r.Index).Distinct().SequenceEqual(new[] { "CBL/SAT", "DVD" }));
        Check("each cell keeps its own value",
            assign.Rows.Single(r => r.Index == "DVD" && r.Name == "HDMI").Value == "HD2");

        // Hide Sources has a row of two plain cells - "CBL/SAT | ZONE 2" - that is
        // not a header. Reading it as one turned every source below into a cell.
        var hide = AspSetupClient.Parse("""
            <FORM action="s_Delete.asp"><TABLE>
            <TR><TD><b>&nbsp;&nbsp;CBL/SAT     </b></TD><TD height='30'> ZONE 2</TD></TR>
            <TR><TD><b>&nbsp;&nbsp;DVD         </b></TD><TD><INPUT type='radio' name='DVD' value='USE' checked>Show<INPUT type='radio' name='DVD' value='DEL'>Hide</TD></TR>
            <TR><TD><b>&nbsp;&nbsp;Blu-ray     </b></TD><TD><INPUT type='radio' name='BD' value='USE' checked>Show<INPUT type='radio' name='BD' value='DEL'>Hide</TD></TR>
            </TABLE></FORM>
            """)!;
        Check("two plain cells are not a header", hide.Rows.All(r => r.Index is null));
        Check("the sources stay a list",
            hide.Rows.Select(r => r.Label).SequenceEqual(new[] { "DVD", "Blu-ray" }));

        // Source Level opens its table with a cell and no row at all.
        var rowless = AspSetupClient.Parse("""
            <FORM action="s_inputsetup.asp"><TABLE><TD height='30'><B>Source Level</B></TD>
            <TD height='30'><INPUT type='text' name='textSourceLevelDigital' value='0'>dB</TD></TABLE></FORM>
            """)!;
        Check("a table with no row still reads", rowless.Rows.Count == 1);
        Check("and reads correctly",
            rowless.Rows[0].Label == "Source Level" && rowless.Rows[0].Value == "0");

        // Channel levels are sliders driving a hidden field, which is the real name.
        var levels = AspSetupClient.Parse("""
            <FORM action="s_speakersetup.asp"><TABLE>
            <TR><TD><B>Front L</B></TD><TD><input id='RangeCVFL' type='range' value='-1.5' min='-12' max='12' step='0.5'/><span>-1.5</span><INPUT type='hidden' name='textCVFL' value='-1.5'></TD></TR>
            </TABLE></FORM>
            """)!;
        Check("a slider is read", levels.Rows.Count == 1 && levels.Rows[0].Value == "-1.5");
        Check("it takes its companion's name", levels.Rows[0].Name == "textCVFL");

        // Every page in the catalog points at a content frame, not its frameset.
        Check("the catalog points at content frames",
            AspCatalog.Groups.All(g => g.Path.Contains("/d_") && g.Path.EndsWith(".asp")));
        Check("and never at a submit page",
            AspCatalog.Groups.All(g => !g.Path.Contains("/s_")));
        Check("the catalog has no duplicates",
            AspCatalog.Groups.Select(g => g.Path).Distinct().Count() == AspCatalog.Groups.Count);
    }

    // ---------------------------------------------------------------- checks

    private static void ProtocolParsing()
    {
        var client = NewClient();

        Feed(client, "PWON", "ZMON", "MV455", "MVMAX 80", "MUOFF", "SISAT/CBL", "MSDOLBY DIGITAL");

        Check("power on", client.State.Power == true);
        Check("main zone on", client.State.Main.Power == true);
        Check("volume half step", client.State.Main.Volume == 45.5);
        Check("volume max", client.State.VolumeMax == 80);
        Check("not muted", client.State.Main.Mute == false);
        Check("source", client.State.Main.Source == "SAT/CBL");
        Check("sound mode", client.State.SoundMode == "DOLBY DIGITAL");

        Feed(client, "MV45");
        Check("whole step volume", client.State.Main.Volume == 45);

        // MVMAX must not be read as a volume.
        Feed(client, "MVMAX 98");
        Check("MVMAX is not the volume", client.State.Main.Volume == 45 && client.State.VolumeMax == 98);

        // Quick Select arrives on the MS prefix but is not a sound mode.
        Feed(client, "MSQUICK3");
        Check("quick select", client.State.QuickSelect == 3 && client.State.SoundMode == "DOLBY DIGITAL");
    }

    private static void ZoneParsing()
    {
        var client = NewClient();
        Feed(client, "Z2ON", "Z235", "Z2MUON", "Z2SAT/CBL", "Z3OFF", "Z330");

        Check("zone 2 power", client.State.Zone2.Power == true);
        Check("zone 2 volume", client.State.Zone2.Volume == 35);
        Check("zone 2 mute", client.State.Zone2.Mute == true);
        Check("zone 2 source", client.State.Zone2.Source == "SAT/CBL");
        Check("zone 3 power", client.State.Zone3.Power == false);
        Check("zone 3 volume", client.State.Zone3.Volume == 30);
        Check("zone 3 seen", client.State.Zone3Seen);
    }

    private static void ParameterParsing()
    {
        var client = NewClient();
        Feed(client,
            "PSBAS 56", "PSTRE 44", "PSSWL 505", "PSDYNEQ ON", "PSDYNVOL MED",
            "PSMULTEQ:FLAT", "PSRSTR MODE2", "PSTONE CTRL ON",
            "PSCINEMA EQ.ON", "PSLOM OFF", "PSDRC HI",
            "CVFL 50", "CVC 505", "CVEND", "SLP 060");

        Check("bass", client.State.Audio.Bass == 56);
        Check("treble", client.State.Audio.Treble == 44);
        Check("subwoofer half step", client.State.Audio.Subwoofer == 50.5);
        Check("dynamic eq", client.State.Audio.DynamicEq == true);
        Check("dynamic volume", client.State.Audio.DynamicVolume == "MED");
        Check("multeq", client.State.Audio.MultEq == "FLAT");
        Check("restorer", client.State.Audio.Restorer == "MODE2");
        Check("tone control", client.State.Audio.ToneControl == true);

        // Cinema EQ is the one parameter whose value is glued to its key.
        Check("cinema eq", client.Parameter("CINEMA EQ.") == "ON");
        Check("loudness management", client.Parameter("LOM") == "OFF");
        Check("dynamic compression", client.Parameter("DRC") == "HI");

        Check("channel trim", client.State.Audio.Channels["FL"] == 50);
        Check("channel half step", client.State.Audio.Channels["C"] == 50.5);
        Check("CVEND is not a channel", !client.State.Audio.Channels.ContainsKey("END"));
        Check("sleep timer", client.State.SleepMinutes == 60);

        Feed(client, "SLPOFF");
        Check("sleep off", client.State.SleepMinutes is null);
    }

    private static void SystemParsing()
    {
        var client = NewClient();
        Feed(client, "ECOAUTO", "DIM DAR", "STBY30M", "VSMONI2", "VSAUDIO TV", "SVSOURCE",
                     "TFAN09790", "TPAN03", "MNMEN ON");

        Check("eco", client.State.System.Eco == "AUTO");
        Check("dimmer", client.State.System.Dimmer == "DAR");
        Check("auto standby", client.State.System.AutoStandby == "30M");
        Check("monitor out", client.State.System.MonitorOut == "2");
        Check("hdmi audio", client.State.System.HdmiAudio == "TV");
        Check("video select", client.State.System.VideoSelect == "SOURCE");
        Check("tuner frequency", client.State.System.TunerFrequency == 97.90);
        Check("tuner preset", client.State.System.TunerPreset == 3);
        Check("menu open", client.State.MenuOpen);
    }

    private static void CommandEncoding()
    {
        var client = NewClient();
        client.Config.Settings.MaxVolume = 98;
        client.Config.Settings.ConfirmAbove = null;

        Feed(client, "PWON", "ZMON", "MVMAX 98");

        Check("half-step volume encodes as three digits",
            Sent(client, () => client.SetZoneVolume(1, 45.5)) == "MV455");
        Check("whole volume encodes as two digits",
            Sent(client, () => client.SetZoneVolume(1, 45)) == "MV45");
        Check("zone 2 volume has no MV prefix",
            Sent(client, () => client.SetZoneVolume(2, 30)) == "Z230");
        Check("zone 2 source",
            Sent(client, () => client.SetZoneSource(2, "BD")) == "Z2BD");
        Check("bass command",
            Sent(client, () => client.SetBass(56)) == "PSBAS 56");
        Check("sound mode command",
            Sent(client, () => client.SetSoundMode("STEREO")) == "MSSTEREO");

        // The ceiling must hold even when asked for more.
        client.Config.Settings.MaxVolume = 70;
        Check("volume ceiling holds",
            Sent(client, () => client.SetZoneVolume(1, 90)) == "MV70");

        Check("sleep encodes three digits",
            Sent(client, () => client.SetSleep(30)) == "SLP 030");
        Check("sleep off",
            Sent(client, () => client.SetSleep(null)) == "SLPOFF");

        // Every setting change must be followed by a query, or nothing can be verified.
        client.ClearPending();
        client.SetDynamicEq(true);
        Check("a change is followed by its query",
            client.Pending.Count == 2 && client.Pending[0] == "PSDYNEQ ON" && client.Pending[1] == "PSDYNEQ ?");
    }

    private static void GraphicEqPayloads()
    {
        var state = new GraphicEqState { Channel = "Front", Backend = GraphicEqBackend.AjaxConfig };
        state.Bands["63"] = 2.5;
        state.Bands["1k"] = -3;
        state.Bands["16k"] = 0;

        var ajax = GraphicEqClient.BuildAdjustEq(state);
        Check("eq sends tenths of a dB", ajax.Contains("<Eq63Hz>25</Eq63Hz>"));
        Check("eq sends negative tenths", ajax.Contains("<Eq1kHz>-30</Eq1kHz>"));
        Check("eq carries the channel", ajax.Contains("<Channel>Front</Channel>"));

        var legacy = new GraphicEqState
        {
            Backend = GraphicEqBackend.LegacyAsp,
            SpeakerSelection = "LRS",
            Channel = "FRO",
        };
        legacy.Bands["63"] = 5;
        legacy.Bands["125"] = 2.5;

        var form = GraphicEqClient.BuildLegacyForm(legacy, enabled: true, submit: "setAdjustEQ");
        Check("legacy form sends decimal dB", form.Contains("textGEQ63=5.0"));
        Check("legacy form half step", form.Contains("textGEQ125=2.5"));
        Check("legacy form marks the submit", form.Contains("setAdjustEQ=Set"));
        Check("legacy form leaves others off", form.Contains("setGEQSetDefaults=off"));
        Check("legacy form carries the selection", form.Contains("listGEQSpSelection=LRS"));
    }

    private static void SetupApiParsing()
    {
        // The Information document, as an AVR-X-series unit actually returns it.
        var information = XDocument.Parse("""
            <Information>
              <Audio><SoundMode>Stereo</SoundMode><InputSignal>PCM</InputSignal><Format>2.0</Format></Audio>
              <Video mode="monitor">
                <HDMISignalInfo><Resolution>4K60</Resolution><HDR>HDR10</HDR>
                  <ColorSpace>YCbCr422</ColorSpace><PixelDepth>--- -&gt; ---</PixelDepth></HDMISignalInfo>
                <HDMIMonitor1><Interface>HDMI</Interface><HDR>HDR10/Dolby Vision/HLG</HDR>
                  <Resolutions><Value>1080p</Value><Value>4K</Value></Resolutions></HDMIMonitor1>
              </Video>
              <Zone><MainZone><SelectSource>TV Audio</SelectSource></MainZone>
                <Zone2><SelectSource>CBL/SAT</SelectSource><Volume>2dB</Volume></Zone2></Zone>
              <Firmware><Version>1700-9165-7071-9031</Version><DTSVersion>3.90.50.00</DTSVersion></Firmware>
            </Information>
            """);

        var info = new ReceiverInfo();
        Check("information parsed", ReceiverInfoReader.Parse(information, info));
        Check("sound mode", info.SoundMode == "Stereo");
        Check("resolution", info.Resolution == "4K60");
        Check("empty fields are dropped", info.PixelDepth is null);
        Check("padded audio format is dropped", ReceiverInfo.Clean(" / /.0") is null);
        Check("real audio format is kept", ReceiverInfo.Clean("2.0") == "2.0");
        Check("dashed video field is dropped", ReceiverInfo.Clean(" ---  ->  --- ") is null);
        Check("monitor resolutions", info.MonitorResolutions.Count == 2);
        Check("zone 2 source", info.Zone2Source == "CBL/SAT");
        Check("firmware version", info.FirmwareVersion == "1700-9165-7071-9031");

        // A setting whose choices are a list of named alternatives: the receiver wants
        // the position back, not the text.
        var hdmi = XDocument.Parse("""
            <HDMISetup>
              <HDMIAudioOut display="2" gray="1">1</HDMIAudioOut>
              <VerticalStretch display="1"/>
              <PassThroughSource display="3">
                <Source>5</Source>
                <List><Last>Last</Last><HDMI1>CBL/SAT</HDMI1><HDMI2>DVD</HDMI2>
                  <HDMI3>Blu-ray</HDMI3><HDMI4>Game</HDMI4></List>
              </PassThroughSource>
              <HDMIControl display="3">1</HDMIControl>
            </HDMISetup>
            """);

        var hdmiRows = AjaxConfigClient.Flatten(hdmi);
        var pass = hdmiRows.FirstOrDefault(r => r.Name == "PassThroughSource");
        Check("value and list collapse to one row", pass is not null);
        Check("no stray row for the value element", hdmiRows.All(r => r.Name != "Source"));
        Check("no stray row for the list", hdmiRows.All(r => r.Name != "List"));
        Check("current value read from Source", pass?.Value == "5");
        Check("choices are positional", pass?.Options.Count == 5
            && pass.Options[0].Code == "1" && pass.Options[0].Text == "Last"
            && pass.Options[4].Code == "5" && pass.Options[4].Text == "Game");

        // display of 1 means the setting does not apply to this receiver's setup.
        Check("inapplicable settings are dropped", hdmiRows.All(r => r.Name != "VerticalStretch"));

        // greyed means present but not changeable right now.
        var greyed = hdmiRows.FirstOrDefault(r => r.Name == "HDMIAudioOut");
        Check("greyed settings are shown", greyed is not null);
        Check("greyed settings are not editable", greyed?.Editable == false);

        // Writes, including the indexed form the receiver's own UI uses.
        Check("plain write shape",
            AjaxConfigClient.BuildWrite("HDMISetup", "HDMIControl", null, "2")
                == "<HDMISetup><HDMIControl>2</HDMIControl></HDMISetup>");
        Check("indexed write carries the index",
            AjaxConfigClient.BuildWrite("SpeakerConfig", "Speaker", "0", "3")
                == "<SpeakerConfig><Speaker index=\"0\">3</Speaker></SpeakerConfig>");

        // Each speaker says what it will accept, and they differ: no centre speaker
        // option for the fronts, yes/no for the subwoofer.
        var speakerDoc = XDocument.Parse("""
            <SpeakerConfig>
              <Speaker index="0"><Value>3</Value><List>2</List><List>3</List></Speaker>
              <Speaker index="1"><Value>1</Value><List>1</List><List>2</List><List>3</List></Speaker>
              <Speaker index="2"><Value>5</Value><List>5</List><List>4</List></Speaker>
            </SpeakerConfig>
            """);

        var speakerRows = AjaxConfigClient.Flatten(speakerDoc);
        Check("one row per speaker", speakerRows.Count == 3);
        Check("front offers two codes", speakerRows[0].Options.Select(o => o.Code).SequenceEqual(["2", "3"]));
        Check("centre offers three codes", speakerRows[1].Options.Select(o => o.Code).SequenceEqual(["1", "2", "3"]));
        Check("subwoofer offers its own codes", speakerRows[2].Options.Select(o => o.Code).SequenceEqual(["5", "4"]));
        Check("speaker rows are editable", speakerRows.All(r => r.Editable));
        Check("centre reads None at code 1",
            AjaxLabels.Value("speakers", 3, "Speaker", "1", "1") == "None");
        Check("front has no None",
            !AjaxLabels.Choices("speakers", 3, "Speaker", "0").Any(c => c.Value == "None"));
        // value first, then the row index
        Check("subwoofer reads Yes at its own code",
            AjaxLabels.Value("speakers", 3, "Speaker", "4", "2") == "Yes");
        Check("subwoofer reads No at its own code",
            AjaxLabels.Value("speakers", 3, "Speaker", "5", "2") == "No");

        // Input assign is a table: settings hang off each <Source index="n">, and a
        // write that lost the index would land on the wrong input.
        var assign = XDocument.Parse("""
            <InputAssign>
              <Source index="1"><Name>CBL/SAT</Name><HDMI display="3">3</HDMI><Digital display="3">1</Digital></Source>
              <Source index="2"><Name>DVD</Name><HDMI display="3">4</HDMI><Digital display="3">1</Digital></Source>
            </InputAssign>
            """);

        var assignRows = AjaxConfigClient.Flatten(assign);
        var hdmiCells = assignRows.Where(r => r.Name == "HDMI").ToList();
        Check("a cell per input", hdmiCells.Count == 2);
        Check("cells inherit their input's index",
            hdmiCells[0].Index == "1" && hdmiCells[1].Index == "2");
        Check("inputs are named apart",
            AjaxLabels.Label("inputs", 2, "HDMI", "1", "") != AjaxLabels.Label("inputs", 2, "HDMI", "2", ""));
        // The harvested label of an input-assign cell is the row it sits in, so a
        // column heading taken from it would read "CBL/SAT" across the whole table.
        Check("a column heading is not a row name",
            AjaxLabels.Label("inputs", 2, "HDMI", "1", "") == "CBL/SAT"
            && SetupBrowser.ColumnHeading("HDMI") == "HDMI");
        Check("a run-together heading is split",
            SetupBrowser.ColumnHeading("SpeakerPreset") == "Speaker Preset");
        Check("an acronym stays whole",
            SetupBrowser.ColumnHeading("HDMIZone") == "HDMI Zone");

        // The source's own name is the row heading, not a sixth setting.
        Check("the name column is the row heading",
            SetupBrowser.HeadingColumnOf(assignRows) == "Name");
        Check("a settings column is not mistaken for it",
            SetupBrowser.HeadingColumnOf(assignRows) != "HDMI");

        // Two rows that happen to read alike are settings, not names.
        var sameName = AjaxConfigClient.Flatten(XDocument.Parse("""
            <InputAssign>
              <Source index="1"><Name>CBL/SAT</Name><HDMI display="3">3</HDMI><Mode display="3">1</Mode></Source>
              <Source index="2"><Name>CBL/SAT</Name><HDMI display="3">4</HDMI><Mode display="3">1</Mode></Source>
            </InputAssign>
            """));
        Check("repeated names are not a heading",
            SetupBrowser.HeadingColumnOf(sameName) is null);

        // A row the harvest never saw must not borrow the first row's name.
        Check("an unharvested row keeps its index",
            AjaxLabels.RowName("inputs", 2, "HDMI", "6") is null);
        Check("a harvested row is still named",
            AjaxLabels.RowName("inputs", 2, "HDMI", "2") == "DVD");

        // The SelectionList at the end of input assign is the vocabulary, not a row.
        var withVocabulary = AjaxConfigClient.Flatten(XDocument.Parse("""
            <InputAssign>
              <Source index="1"><Name>CBL/SAT</Name><HDMI display="3">3</HDMI></Source>
              <Source index="2"><Name>DVD</Name><HDMI display="3">4</HDMI></Source>
              <SelectionList>
                <HDMI><List><Item index="1">-</Item><Item index="3">1</Item><Item index="4">2</Item></List></HDMI>
              </SelectionList>
            </InputAssign>
            """));
        Check("the vocabulary is not a row",
            withVocabulary.All(r => r.Index is null or "1" or "2"));
        Check("the vocabulary is not a column",
            withVocabulary.All(r => r.Name != "Item"));
        var hdmiCell = withVocabulary.First(r => r.Name == "HDMI");
        Check("a cell takes the published codes",
            hdmiCell.Options.Select(o => o.Code).SequenceEqual(new[] { "1", "3", "4" }));
        Check("a code keeps the receiver's own word for it",
            hdmiCell.Options.Single(o => o.Code == "3").Text == "1");

        // A row can sit on a code the receiver leaves off its own list.
        var offList = AjaxConfigClient.Flatten(XDocument.Parse("""
            <InputAssign>
              <Source index="1"><Name>CBL/SAT</Name><HDMI display="3">3</HDMI></Source>
              <Source index="6"><Name>TV Audio</Name><HDMI display="2">2</HDMI></Source>
              <SelectionList>
                <HDMI><List><Item index="1">-</Item><Item index="3">1</Item></List></HDMI>
              </SelectionList>
            </InputAssign>
            """));
        var odd = offList.First(r => r.Index == "6" && r.Name == "HDMI");
        Check("an off-list value is still read",
            odd.Value == "2" && odd.Options.All(o => o.Code != "2"));

        // Crossovers publishes the values it will accept, so those rows are editable.
        var crossovers = XDocument.Parse("""
            <Crossovers mode="normal">
              <SpeakerPreset display="1"/>
              <Selection display="3">2</Selection>
              <All display="2">250</All>
              <List><Speaker index="0" display="2">40</Speaker><Speaker index="2" display="3">150</Speaker></List>
              <SelectableValue><List><Item>40</Item><Item>60</Item><Item>80</Item></List></SelectableValue>
            </Crossovers>
            """);

        var rows = AjaxConfigClient.Flatten(crossovers);
        var speakers = rows.Where(r => r.Name == "Speaker").ToList();
        Check("crossover rows found", speakers.Count == 2);
        Check("crossover value", speakers[0].Value == "40");
        Check("crossover is editable", speakers[0].Editable && speakers[0].Options.Count == 3);
        Check("crossover keeps its index", speakers[0].Index == "0" && speakers[1].Index == "2");

        // A document with no published choices must stay read-only.
        var zone = XDocument.Parse("""
            <ZoneSetup index="2"><Treble display="3">0</Treble><VolumeLevel display="3">82</VolumeLevel></ZoneSetup>
            """);
        var zoneRows = AjaxConfigClient.Flatten(zone);
        Check("values with no published choices are read-only", zoneRows.All(r => !r.Editable));
        Check("value read", zoneRows.Any(r => r.Name == "VolumeLevel" && r.Value == "82"));
    }

    private static void LabelTable()
    {
        // Harvested from the receiver's own setup UI, so codes read as words.
        Check("a known code reads as a word",
            AjaxLabels.Value("video", 3, "HDMIControl", "1") == "On");
        Check("an unknown code is left alone",
            AjaxLabels.Value("video", 3, "HDMIControl", "99") == "99");
        Check("an unknown field falls back to the tag",
            AjaxLabels.Label("video", 3, "NoSuchTag", null, "NoSuchTag") == "NoSuchTag");
        Check("indexed rows are named",
            AjaxLabels.Label("speakers", 3, "Speaker", "0", "Speaker 0").Length > 0);
        Check("choices come back for a known field",
            AjaxLabels.Choices("video", 3, "HDMIControl").Count == 2);
    }

    // ---------------------------------------------------------------- helpers

    private static DenonClient NewClient()
    {
        var probe = new HttpProbe(NullLogger<HttpProbe>.Instance);
        var ajax = new AjaxConfigClient(probe, NullLogger<AjaxConfigClient>.Instance);

        return new DenonClient(
            new ReceiverConfig { Host = "test", Name = "test" },
            new DeviceProfileReader(ajax, NullLogger<DeviceProfileReader>.Instance),
            new GraphicEqClient(probe, NullLogger<GraphicEqClient>.Instance),
            ajax,
            new ReceiverInfoReader(ajax),
            NullLoggerFactory.Instance);
    }

    private static void Feed(DenonClient client, params string[] lines)
    {
        foreach (var line in lines) client.Handle(line);
    }

    /// <summary>Runs an action and returns the command it queued for the receiver.</summary>
    private static string? Sent(DenonClient client, Action action)
    {
        client.ClearPending();
        action();
        return client.Pending.FirstOrDefault();
    }

    private static void Check(string what, bool ok)
    {
        if (ok) { _passed++; Console.WriteLine($"  ok    {what}"); }
        else Failures.Add(what);
    }
}
