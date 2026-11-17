using System.Text.Json.Nodes;

namespace FluentEscritor.Api.Ai;

/// <summary>
/// Built-in advanced, intermediate, and beginner micro-drill templates.
/// Ported verbatim from <c>src/lib/ai/examiner.ts</c>.
/// </summary>
internal static class DrillTemplates
{
    public static readonly IReadOnlyDictionary<string, MicroDrillChallenge> Builtin = new Dictionary<string, MicroDrillChallenge>
    {
        ["overview_sprint"] = new()
        {
            DrillType = "overview_sprint",
            TargetCriterion = "TA_TR",
            Title = "4-Minute Task 1 Overview Sprint",
            Instructions = "Inspect the chart below and write ONLY a 2-sentence Academic Task 1 Overview paragraph (35–55 words). Capture the main overall trend and the most striking contrast/dominance—WITHOUT quoting specific percentages or years!",
            TimeLimitSeconds = 240,
            SourceContext = "Global Cargo Shipping Propulsion Mix (2010–2030 Projections)",
            ChartSpec = new JsonObject
            {
                ["kind"] = "line",
                ["title"] = "Share of New Commercial Vessels by Fuel Type (2010–2030)",
                ["xAxisKey"] = "year",
                ["yAxisUnit"] = "%",
                ["series"] = new JsonArray(
                    new JsonObject { ["key"] = "diesel", ["label"] = "Conventional Heavy Fuel Oil", ["color"] = "#475569" },
                    new JsonObject { ["key"] = "lng", ["label"] = "Liquefied Natural Gas (LNG)", ["color"] = "#16656e" },
                    new JsonObject { ["key"] = "methanol", ["label"] = "Green Methanol & Ammonia", ["color"] = "#228b5b" }),
                ["data"] = new JsonArray(
                    new JsonObject { ["year"] = "2010", ["diesel"] = 96, ["lng"] = 3, ["methanol"] = 0 },
                    new JsonObject { ["year"] = "2015", ["diesel"] = 89, ["lng"] = 9, ["methanol"] = 1 },
                    new JsonObject { ["year"] = "2020", ["diesel"] = 74, ["lng"] = 22, ["methanol"] = 3 },
                    new JsonObject { ["year"] = "2025", ["diesel"] = 51, ["lng"] = 33, ["methanol"] = 14 },
                    new JsonObject { ["year"] = "2030", ["diesel"] = 28, ["lng"] = 37, ["methanol"] = 35 }),
            },
            Hints =
            [
                "Start with a clear signpost such as 'Overall,' or 'In general,'",
                "Contrast the steep decline in conventional oil against the surge in cleaner alternatives, noting that LNG and green fuels are projected to overtake heavy oil by the end of the period.",
            ],
            ReferenceModelAnswer = "Overall, reliance on conventional heavy fuel oil for new commercial vessels declines precipitously across the timeframe, whereas cleaner alternative fuels experience substantial growth. Notably, although heavy oil initially dominated orders, both liquefied natural gas and green methanol-ammonia are projected to surpass it by the end of the period.",
        },
        ["cohesion"] = new()
        {
            DrillType = "cohesion",
            TargetCriterion = "CC",
            Title = "Cohesion De-Mechanizer",
            Instructions = "Rewrite this Band 6.0 paragraph to achieve Band 8.0+ Coherence & Cohesion. Eliminate ALL four mechanical sentence-initial linkers ('Firstly', 'Moreover', 'On the other hand', 'In conclusion') using demonstrative referencing ('This influx...', 'Such constraints...'), subordination, and thematic flow.",
            TimeLimitSeconds = 300,
            SourceContext = "Task 2 Body Paragraph on Remote Work & Regional Towns",
            FlawedText = "Firstly, remote work allows salaried professionals to relocate away from overcrowded capitals into smaller towns. Moreover, these new residents spend their salaries in local cafés, clinics, and schools, which revives regional economies. On the other hand, sudden population inflows can push up local rents rapidly. Because of this, long-standing town residents may struggle to afford housing.",
            Hints =
            [
                "Link Sentence 1 → 2 via nominalisation ('This geographic dispersal injects disposable income...')",
                "Turn 'On the other hand... Because of this...' into a tightly integrated concessive or participial complex sentence.",
            ],
            ReferenceModelAnswer = "Widespread telecommuting enables salaried professionals to relocate from congested capitals to smaller regional municipalities, injecting vital consumer spending into local cafés, clinics, and schools. Yet this demographic influx carries an acute housing trade-off: rapid demand growth can inflate rental prices, potentially pricing long-standing residents out of their own communities.",
        },
        ["l1_surgeon"] = new()
        {
            DrillType = "l1_surgeon",
            TargetCriterion = "L1",
            Title = "Spanish → English Clause & False-Cognate Surgeon",
            Instructions = "Locate and fix 4 classic L1 interference slips in the passage below (false cognates, comma splices with 'however', definite article generalisation, and preposition calque) while elevating the prose to crisp Band 8.0 academic register.",
            TimeLimitSeconds = 300,
            SourceContext = "Task 2 Paragraph on University Curriculum Reform",
            FlawedText = "Actually, the society demands graduates who possess practical digital literacy, however, many public universities still assist to outdated lecture models that depend of rote memorisation rather than collaborative problem-solving.",
            Hints =
            [
                "'Actually' ≠ 'actualmente' (use 'Currently / At present')",
                "'the society' → abstract plural/uncountable generalisation drops 'the'",
                "', however,' between two full clauses is a comma splice",
                "'assist to' (asistir a) & 'depend of' (depender de) are Romance calques",
            ],
            ReferenceModelAnswer = "Currently, modern economies demand graduates equipped with practical digital literacy; however, many public universities still adhere to outdated lecture formats that rely on rote memorisation rather than collaborative problem-solving.",
        },
        ["lexical_hedging"] = new()
        {
            DrillType = "lexical_hedging",
            TargetCriterion = "LR",
            Title = "Band 6.5 → 8.0 Lexical & Hedging Upgrade",
            Instructions = "Transform these sweeping, informal Band 6.0 claims into nuanced, academically hedged Band 8.0+ arguments without changing the underlying stance.",
            TimeLimitSeconds = 300,
            SourceContext = "Task 2 Claims on Congestion Taxes & Public Health",
            FlawedText = "Putting big taxes on sugary drinks will definitely stop obesity in every country. People will stop buying sodas right away, and governments will get lots of money to fix all hospital problems.",
            Hints =
            [
                "Replace absolutes ('will definitely stop', 'every', 'all') with calibrated modal/lexical hedging ('can significantly curb', 'tends to discourage', 'earmarked revenue')",
                "Upgrade informal lexis ('putting big taxes', 'lots of money', 'fix hospital problems') to fiscal/public-health collocations.",
            ],
            ReferenceModelAnswer = "Levying tiered excise duties on sugar-sweetened beverages can measurably curb caloric intake, particularly among price-sensitive consumers. While fiscal disincentives alone rarely eradicate obesity, the resulting tax receipts can be earmarked to subsidise preventative healthcare and nutritional education.",
        },
    };

    public static readonly IReadOnlyDictionary<string, MicroDrillChallenge> Beginner = new Dictionary<string, MicroDrillChallenge>
    {
        ["overview_sprint"] = new()
        {
            DrillType = "overview_sprint",
            Level = "beginner",
            TargetCriterion = "TA_TR",
            Title = "Write a Chart Summary (2 Sentences)",
            Instructions = "Look at the chart. Write 2 short sentences that say what changed in general. Do not write any numbers.",
            TimeLimitSeconds = 300,
            SourceContext = "Cinema vs. Streaming: How People Watch Films (2015–2035)",
            ChartSpec = new JsonObject
            {
                ["kind"] = "bar",
                ["title"] = "How People Watch New Films (% of viewers)",
                ["xAxisKey"] = "year",
                ["yAxisUnit"] = "%",
                ["series"] = new JsonArray(
                    new JsonObject { ["key"] = "cinema", ["label"] = "Cinema", ["color"] = "#475569" },
                    new JsonObject { ["key"] = "streaming", ["label"] = "Streaming at home", ["color"] = "#16656e" }),
                ["data"] = new JsonArray(
                    new JsonObject { ["year"] = "2015", ["cinema"] = 70, ["streaming"] = 30 },
                    new JsonObject { ["year"] = "2020", ["cinema"] = 55, ["streaming"] = 45 },
                    new JsonObject { ["year"] = "2025", ["cinema"] = 40, ["streaming"] = 60 },
                    new JsonObject { ["year"] = "2030", ["cinema"] = 30, ["streaming"] = 70 },
                    new JsonObject { ["year"] = "2035", ["cinema"] = 25, ["streaming"] = 75 }),
            },
            Steps =
            [
                new() { Prompt = "Look at the bars. Which one goes up? Which one goes down?" },
                new() { Prompt = "Write one sentence about the general change. Start with “Overall,”.", Starter = "Overall, the share of people who ___ increased, while ___ decreased." },
                new() { Prompt = "Write one more sentence about which one is bigger at the end.", Starter = "By the end of the period, ___ was the most popular way to watch films." },
            ],
            WordBank = ["Overall,", "increased", "decreased", "rose steadily", "fell", "while", "became more popular than", "by the end of the period"],
            Hints =
            [
                "Start your first sentence with “Overall,”.",
                "Use “while” to compare two things in one sentence.",
                "No numbers! Say “increased” or “fell”, not “70%”.",
            ],
            ReferenceModelAnswer = "Overall, the share of people who watch new films by streaming at home increased, while the share who go to the cinema decreased. By the end of the period, streaming had become much more popular than the cinema.",
        },
        ["cohesion"] = new()
        {
            DrillType = "cohesion",
            Level = "beginner",
            TargetCriterion = "CC",
            Title = "Make Your Sentences Flow",
            Instructions = "This paragraph has 2 linking words that sound like a list (“Firstly”, “Also”). Rewrite it so the sentences connect more naturally.",
            TimeLimitSeconds = 300,
            SourceContext = "Short paragraph about public parks",
            FlawedText = "Firstly, parks give people a place to exercise. Also, parks are good for children. Children can play outside and make friends.",
            Steps =
            [
                new() { Prompt = "Find the 2 list words: “Firstly” and “Also”." },
                new() { Prompt = "Remove “Firstly”. Start the paragraph with the main idea instead.", Starter = "Parks give people a free place to exercise." },
                new() { Prompt = "Connect the next ideas. Use “This…”, “In addition,” or “where”.", Starter = "They are also important for children, who can ___" },
            ],
            WordBank = ["This", "These spaces", "In addition,", "where", "who", "because", "As a result,"],
            Hints =
            [
                "You do not need “Firstly” in a short paragraph. Just start with your idea.",
                "“This” or “These spaces” can point back to the last sentence.",
                "You can join two short sentences with “where” or “who”.",
            ],
            ReferenceModelAnswer = "Parks give people a free place to exercise. They are also important for children, who can play outside and make new friends there.",
        },
        ["l1_surgeon"] = new()
        {
            DrillType = "l1_surgeon",
            Level = "beginner",
            TargetCriterion = "L1",
            Title = "Fix 2 Spanish-to-English Mistakes",
            Instructions = "This sentence has 2 common mistakes that Spanish speakers make. Find them and write the correct sentence.",
            TimeLimitSeconds = 300,
            SourceContext = "One sentence about technology",
            FlawedText = "Actually, the technology is very important in our lives.",
            Steps =
            [
                new() { Prompt = "Look at the first word. Does “Actually” mean “actualmente”?" },
                new() { Prompt = "Look at “the technology”. Do we need “the” when we talk about technology in general?" },
                new() { Prompt = "Write the correct sentence.", Starter = "Nowadays, ___ is very important in our lives." },
            ],
            WordBank = ["Nowadays,", "Currently,", "At present,", "technology", "plays an important role in"],
            Hints =
            [
                "“Actually” means “en realidad”. For “actualmente”, use “Nowadays” or “Currently”.",
                "In English, general ideas have no “the”: “Technology is…”, not “The technology is…”.",
            ],
            ReferenceModelAnswer = "Nowadays, technology plays a very important role in our lives.",
        },
        ["lexical_hedging"] = new()
        {
            DrillType = "lexical_hedging",
            Level = "beginner",
            TargetCriterion = "LR",
            Title = "Make Strong Claims Softer",
            Instructions = "These sentences say “always” and “all”, which sounds too strong. Rewrite them so they are more careful and a little more formal.",
            TimeLimitSeconds = 300,
            SourceContext = "Two sentences about video games",
            FlawedText = "Video games are always bad for children. All kids who play them get lots of bad habits.",
            Steps =
            [
                new() { Prompt = "Find the very strong words: “always”, “All”, “lots of”." },
                new() { Prompt = "Change them to softer words like “can”, “some”, “many”.", Starter = "Video games can be ___ for children." },
                new() { Prompt = "Change one informal word (“kids” or “lots of”) to a more formal word.", Starter = "Some children who play them for many hours may ___" },
            ],
            WordBank = ["can", "may", "some", "many", "often", "children", "a number of", "harmful", "develop"],
            Hints =
            [
                "“always” → “can” or “often”. “All” → “Some” or “Many”.",
                "“kids” → “children”. “lots of” → “a number of” or “several”.",
            ],
            ReferenceModelAnswer = "Video games can be harmful for children. Some children who play them for many hours may develop bad habits.",
        },
    };

    private static MicroDrillChallenge BeginnerL1(
        string title,
        string instructions,
        string flawedText,
        List<MicroDrillStep> steps,
        List<string> wordBank,
        List<string> hints,
        string referenceModelAnswer) => new()
    {
        DrillType = "l1_surgeon",
        Level = "beginner",
        TargetCriterion = "L1",
        Title = title,
        Instructions = instructions,
        TimeLimitSeconds = 300,
        SourceContext = "One short sentence to correct",
        FlawedText = flawedText,
        Steps = steps,
        WordBank = wordBank,
        Hints = hints,
        ReferenceModelAnswer = referenceModelAnswer,
    };

    public static readonly IReadOnlyDictionary<string, MicroDrillChallenge> BeginnerL1Templates = new Dictionary<string, MicroDrillChallenge>
    {
        ["es"] = Beginner["l1_surgeon"],
        ["pt"] = BeginnerL1(
            "Fix 2 Portuguese-to-English Mistakes",
            "This sentence has 2 common mistakes that Portuguese speakers make. Find them and write the correct sentence.",
            "Actually, the technology is very important in our lives.",
            [
                new() { Prompt = "Look at the first word. Does “Actually” mean “atualmente”?" },
                new() { Prompt = "Look at “the technology”. Do we need “the” when we talk about technology in general?" },
                new() { Prompt = "Write the correct sentence.", Starter = "Nowadays, ___ is very important in our lives." },
            ],
            ["Nowadays,", "Currently,", "At present,", "technology", "plays an important role in"],
            [
                "“Actually” means “na verdade”. For “atualmente”, use “Nowadays” or “Currently”.",
                "In English, general ideas have no “the”: “Technology is…”, not “The technology is…”.",
            ],
            "Nowadays, technology plays a very important role in our lives."),
        ["fr"] = BeginnerL1(
            "Fix 2 French-to-English Mistakes",
            "This sentence has 2 common mistakes that French speakers make. Find them and write the correct sentence.",
            "Actually, the technology is very important in our lives.",
            [
                new() { Prompt = "Look at the first word. Does “Actually” mean “actuellement”?" },
                new() { Prompt = "Look at “the technology”. Do we need “the” when we talk about technology in general?" },
                new() { Prompt = "Write the correct sentence.", Starter = "Nowadays, ___ is very important in our lives." },
            ],
            ["Nowadays,", "Currently,", "At present,", "technology", "plays an important role in"],
            [
                "“Actually” means “en fait”. For “actuellement”, use “Nowadays” or “Currently”.",
                "In English, general ideas have no “the”: “Technology is…”, not “The technology is…”.",
            ],
            "Nowadays, technology plays a very important role in our lives."),
        ["zh"] = BeginnerL1(
            "Fix 2 Plural & Verb Mistakes",
            "This sentence has 2 common mistakes that Mandarin speakers make with plurals and verbs. Find them and write the correct sentence.",
            "Many student needs a computer for study.",
            [
                new() { Prompt = "Look at “Many student”. After “many”, should the noun be singular or plural?" },
                new() { Prompt = "Now look at the verb “needs”. Does it match a plural subject?" },
                new() { Prompt = "Write the correct sentence.", Starter = "Many ___ need a computer for ___." },
            ],
            ["students", "need", "studying", "their studies", "many", "a computer"],
            [
                "After “many”, use a plural noun: “many students”.",
                "Plural subjects take the verb without “-s”: “students need”, not “students needs”.",
            ],
            "Many students need a computer for their studies."),
        ["ar"] = BeginnerL1(
            "Fix 2 Sentence-Pattern Mistakes",
            "This sentence has 2 common mistakes that Arabic speakers make. Find them and write the correct sentence.",
            "This is the policy which the government made it, and the people like it.",
            [
                new() { Prompt = "Look at “which the government made it”. Do we need “it” after “which”?" },
                new() { Prompt = "Look at “the people”. Are we talking about specific people or people in general?" },
                new() { Prompt = "Write the correct sentence.", Starter = "This is the policy which the government made, and ___ like it." },
            ],
            ["which", "that", "many people", "people", "most people", "support"],
            [
                "“which” already points to “the policy”, so do not add “it” at the end.",
                "For people in general, do not use “the”: “people like it” or “many people like it”.",
            ],
            "This is the policy which the government made, and many people like it."),
        ["hi"] = BeginnerL1(
            "Fix 2 Preposition Mistakes",
            "This sentence has 2 common mistakes that Hindi speakers make with small words. Find them and write the correct sentence.",
            "Students should discuss about the problem and cope up with stress.",
            [
                new() { Prompt = "Look at “discuss about”. Does “discuss” need “about” after it?" },
                new() { Prompt = "Look at “cope up with”. Is “up” needed?" },
                new() { Prompt = "Write the correct sentence.", Starter = "Students should discuss ___ and cope with ___." },
            ],
            ["discuss", "cope with", "talk about", "deal with", "the problem", "stress"],
            [
                "Say “discuss the problem” (no “about”), or “talk about the problem”.",
                "The correct phrase is “cope with”, not “cope up with”.",
            ],
            "Students should discuss the problem and cope with stress."),
        ["neutral"] = BeginnerL1(
            "Fix 2 Small Grammar Mistakes",
            "This sentence has 2 common small mistakes with “the”. Find them and write the correct sentence.",
            "The technology is very important in the our lives.",
            [
                new() { Prompt = "Look at “The technology”. Do we need “the” when we talk about technology in general?" },
                new() { Prompt = "Look at “the our lives”. Can “the” and “our” go together?" },
                new() { Prompt = "Write the correct sentence.", Starter = "___ is very important in ___ lives." },
            ],
            ["Technology", "our", "plays an important role in", "very important", "daily lives"],
            [
                "In English, general ideas have no “the”: “Technology is…”.",
                "Use “the” or “our”, never both: “our lives”.",
            ],
            "Technology plays a very important role in our lives."),
    };

    public static readonly IReadOnlyDictionary<string, List<MicroDrillStep>> IntermediateSteps = new Dictionary<string, List<MicroDrillStep>>
    {
        ["overview_sprint"] =
        [
            new() { Prompt = "Find the biggest overall trend: which category rises most, and which falls most?" },
            new() { Prompt = "Write sentence 1 about the general change, starting with “Overall,” (no numbers)." },
            new() { Prompt = "Write sentence 2 about the most striking contrast or the ranking at the end of the period." },
        ],
        ["cohesion"] =
        [
            new() { Prompt = "Find the 4 list-style linkers (“Firstly”, “Moreover”, “On the other hand”, “Because of this”)." },
            new() { Prompt = "Join sentences 1 and 2, using “This…” or “These…” to point back to the previous idea." },
            new() { Prompt = "Merge the last two sentences into one contrast sentence starting with “Yet” or “However,”." },
        ],
        ["l1_surgeon"] =
        [
            new() { Prompt = "Read word by word and underline anything that looks like a direct translation from your first language." },
            new() { Prompt = "Check the joins between clauses: is “however” between two full sentences with only commas around it?" },
            new() { Prompt = "Rewrite the sentence with each mistake fixed, keeping the same meaning." },
        ],
        ["lexical_hedging"] =
        [
            new() { Prompt = "Underline every absolute word (“definitely”, “every”, “all”, “right away”)." },
            new() { Prompt = "Replace them with careful language: “can”, “may”, “tends to”, “many”." },
            new() { Prompt = "Swap informal phrases (“big taxes”, “lots of money”) for more formal ones." },
        ],
    };
}
