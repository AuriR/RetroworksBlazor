namespace RetroWorks.Core;

/// <summary>
/// Lightweight built-in spell checker for the Word Processor.
/// AppleWorks 4.0 on the Apple II had no built-in spell check (it was a
/// third-party add-on), so this is a self-contained retro-style implementation:
/// a compact English dictionary, word extraction, and simple suggestions.
/// Only Latin-script words are checked; Hebrew text is skipped by design.
/// </summary>
public static class SpellCheck
{
    /// <summary>A run of letters (optionally with an internal apostrophe), e.g. "don't".</summary>
    private static readonly System.Text.RegularExpressions.Regex WordRe = new(
        @"[A-Za-z]+(?:'[A-Za-z]+)?", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Compact English dictionary (lowercase). Common words only — deliberately small.</summary>
    public static readonly HashSet<string> Dict = BuildDict();

    private static HashSet<string> BuildDict() => new(WordList.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    // ~1,200 of the most common English words (lowercase).
    private const string WordList = """
        a about above across after again against all almost alone along already also always am among and another any
        anyone anything anywhere are around as ask at back be because been before behind being below between big both
        bring but by came can cannot come could day did different do does doing done down during each even every eye
        face felt few find first for found from front get give go going good got great had has have having he her here
        high him himself his how i if in into is it its itself just keep knew know last left let like line look made
        make many may me might minute miss much must my near need never new next no not now off often old one only open
        or other our out over own part people place put quite right said same saw say see she should show side since
        small so some still such take tell than that the their them then there these they thing think this those through
        time to today together too took turn two under until up upon us use used very want was way we well went where
        which while who why will with within without word work world would year yes yet you young your
        able ahead along always among around because become before behind below beside besides between beyond black blue
        brown clear color common complete consider continue correct create current dark deep describe detail develop
        direct early easy enough entire especially example expect explain family final first follow forget free front
        full general give ground group happen happy hard health heavy help high home hope house human important inside
        interest keep kind large last late learn left light list long look make man mean member mind month more morning
        mother move much music name near need night north number off open page paper parent past phone picture place
        plan play point power problem program public question quick read ready real reason record red remember right
        river room rule run second see season side sister sit six size sleep sound south space speak special spring
        stand start state station step story street strong study summer sun table teacher tell test text than thank
        think third thousand three through today together tonight town tree true turn two type unit until upon usual
        voice water week weight white whole winter word work world write year yellow young
        accept achieve act add address admit adopt affect afford agree alert allow alter amount analyze anchor announce
        answer appear apply appoint approach approve argue arrange arrive assess assign assume attach attend attract
        avoid award aware back balance bank base basic beat become begin believe benefit bind birth body book border
        borrow bound box break brief bridge bring broadcast budget build burn burst button buy cable campaign camp
        cancel candle cap capital capture career carry case cash cast catch cause ceiling celebrate center chain chair
        challenge channel charge chase check chest chief child choice choose circle citizen claim class clean click
        client climb clock close cloth cloud coat code coffee cold collect college color column combine come comment
        commit community company compare compete complete complex compose compute concern concentrate confirm connect
        consider contain contract control convince cook cool copy corner cost count country couple course cover crack
        cream credit crime crop cross crowd crown cruise culture cup curve cycle dance danger dash data date deal debt
        decide declare decorate decrease defend define degree delay deliver demand depend deposit describe design desire
        detect determine develop device devote digest digital dinner direct discover discuss disease display distance
        divide document domain double doubt draft drag draw dream dress drink drive drop drum dry due dust duty eagle
        early earn earth ease east easy echo edge effect effort eight either elbow elder elect element elite else emit
        employ enable encourage end enemy engage engine enhance enjoy enough enter entire entry equal escape essay estate
        evaluate evening event ever every exact exam example excess exchange excite exclude execute excuse exist expand
        expect expire explain explore express extend extra eye face facility fact factor fail fair faith fall false fame
        fan far farm fast father fault favor fear feature feed feel fellow female fence fight figure file fill film final
        finance find finger finish fire firm first fish fit five fix floor flower fly focus follow food foot force foreign
        forget form forward found four frame free fresh friend front fruit fuel full fund future gain game garden gas gate
        gather gear gender gene gentle gift girl give glass goal gold good government grade grain grand grass gray great
        green ground group grow guard guess guide gun hair half hall hand handle hang happen happy hard harm hat hate head
        health hear heart heat heavy hello help hen hero hide high hill hint hire history hit hold hole holy home hope horn
        hospital host hour huge human hundred hungry hunt hurt husband ice idea image impact improve include income increase
        indicate industry individual influence inform initial inside insight inspire install instance instruct integrate
        intend interact interest interpret involve issue item jacket job join joke journal judge jump junior jury just key
        kick kid kind kitchen knee knife knock know label labor ladder land language laptop large laser late laugh layer lead
        leaf learn least leave legal lemon length less letter level library license life lift light limit line link listen
        little live load loan local logic loose lose loss loud love lower luck lunch machine magazine mail main maintain major
        make male manage manner map march mark market marry mass match material matter may meal mean measure media medical meet
        member memory mention menu message metal method middle milk million mind minute mirror miss mission mix model modern
        moment money monitor month moral more mother motion motor mountain mouse mouth move movie muscle music must name narrow
        nation natural near neck need needle network nerve nest never news next nice night node noise north note nothing notice
        novel now number object occur ocean offer office often oil old once only open option order organ origin other outside owner
        pack page paint pair panel paper parent park part party pass past path pattern pause pay peace pen people pepper perfect
        perform period permit person phone photo phrase pick piece pilot pin pink pipe pitch place plan plant plastic plate play
        please plug pocket poem point pole police policy poll pond pool poor pop port position possible post power prepare press
        prevent price private prize probably problem process produce profit program project promote protect prove provide public
        pull pump punch purchase purpose push quality quarter queen question quick quit quote race radio rain raise range rate reach
        read ready real reason receive record recover reduce refer reflect region relate release remain remember remove repeat replace
        report require research resource respond rest result return reveal review rich ride ring rise risk road rock role roll room
        root route row rubber rule run rush safe sail sale salt sample sand save scale scan scene school science score screen sea
        search season seat second section select sell send sense series serve set settle seven shadow shake shape share sharp sheet
        shell shift shine ship shirt shock shoot shop shore short shot should show side signal silent silver simple since sing single
        sister sit site six size skill skin sky sleep slide slight small smart smoke smooth snow social sock soft soil solar solid solve
        some song soon sort soul sound source south space spare speak special speed spell spend sport spread spring square stack staff
        stage stain stand star start state station stay steady steal steam steel stem step stick still stock stone stood stool stop store
        storm story straight street stress stretch strike strong structure struggle student stuff style subject submit success such sudden
        suggest suit summer sun supply support suppose sure surface surgery switch symbol system table tail take tale talk tall tank tape
        target task taste tax teach team tear tell ten term test text than thank theme then theory thing think third this those three throw
        thumb thus ticket tide tie time tiny tip tire title to today together tooth top topic total touch toward town track trade train transfer
        travel treat tree trend trial tribe trick trip trouble trust truth tube turn twin type typical ugly unit uniform union unique unite universal
        university until unusual update upon upper urban usual utility various vehicle version very video view village violence virtual visit visual
        vital voice volume vote wait wake walk wall want war warm warn wash waste watch water wave way wealth wear weather week weight welcome west wet
        what wheel when where whether which while white who whole why wide wife wild will wind window wine wing winter wire wish with within woman wonder
        wood word work world worry worth would wound write wrong yard year yellow yes yesterday yield young your youth zone
        """;

    /// <summary>Yield every Latin word in the paragraphs with its location.</summary>
    public static IEnumerable<(int pi, int start, int end, string word)> FindWords(IReadOnlyList<string> paras)
    {
        for (int i = 0; i < paras.Count; i++)
            foreach (System.Text.RegularExpressions.Match m in WordRe.Matches(paras[i]))
                yield return (i, m.Index, m.Index + m.Length, m.Value);
    }

    /// <summary>Next Latin word at or after document position (fromPi, fromCi), or null.</summary>
    public static (int pi, int start, int end, string word)? NextWord(IReadOnlyList<string> paras, int fromPi, int fromCi)
    {
        for (int i = Math.Max(0, fromPi); i < paras.Count; i++)
        {
            foreach (System.Text.RegularExpressions.Match m in WordRe.Matches(paras[i]))
            {
                if (i == fromPi && m.Index < fromCi) continue;
                return (i, m.Index, m.Index + m.Length, m.Value);
            }
        }
        return null;
    }

    /// <summary>True if the word is acceptable. Short all-caps words pass as acronyms.</summary>
    public static bool IsKnown(string word)
    {
        if (word.Length <= 3 && word == word.ToUpperInvariant()) return true;   // OK, I, USA...
        return Dict.Contains(word.ToLowerInvariant());
    }

    /// <summary>Up to `max` dictionary suggestions within edit distance 2.</summary>
    public static List<string> Suggest(string word, int max = 3)
    {
        string w = word.ToLowerInvariant();
        var best = new List<(int dist, string cand)>();
        foreach (var c in Dict)
        {
            if (Math.Abs(c.Length - w.Length) > 2) continue;
            int d = Dist(w, c);
            if (d is > 0 and <= 2) best.Add((d, c));
        }
        return best.OrderBy(x => x.dist).ThenBy(x => x.cand).Take(max).Select(x => x.cand).ToList();
    }

    /// <summary>Levenshtein distance (bounded early-exit at limit=2).</summary>
    private static int Dist(string a, string b, int limit = 2)
    {
        int n = a.Length, m = b.Length;
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;
        for (int i = 1; i <= n; i++)
        {
            cur[0] = i;
            int rowMin = cur[0];
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (cur[j] < rowMin) rowMin = cur[j];
            }
            if (rowMin > limit) return limit + 1;   // can't improve beyond the bound
            (prev, cur) = (cur, prev);
        }
        return prev[m];
    }
}
