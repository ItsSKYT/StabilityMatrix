using System.Reflection;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;

var prompt = """
Photo focused on (one female subject:2.5).
Action: She is (lying on her back:1.5) in bed, one hand slipping the strap off her shoulder, the fabric sliding loose down her arm, chin tipped toward the bared shoulder, (with both legs stretched long and ankles loosely crossed:1.5). (Her head is resting on the pillow, hair spread behind her.:1.5)
Subject: An     girl (Polish-Scottish  girl:2.5) with warm gen blonde hair pulled back into a low ponytail, center-parted, with a loose face-framing strand falling beside one cheek, the ponytail falling well past her shoulders. She has a delicate face with high cheekbones and a pointed chin and big bright hazel eyes; thin high-arched brows, a  refined nose and full matte wine-red lips. Soft dewy glam makeup..
Expression: Expression: A, sulky playful pout, her lips pushed forward and to one side in an exaggerated pucker that dimples her chin. Her eyes roll pointedly away in the same direction, lids relaxed under level brows.
(She is clothed.:1.5)
Body: she has a ( narrow torso:1.3), (narrow waist:1.5), (extremely wide full jutting hips:1.8), (plump round thick thighs:1.8). She is .
Breasts: She has (huge oversized breasts:2.2) with (a perky, angular upward apex shape:2.5)..
Clothing: She is wearing a A casually sultry ribbed maroon long-sleeve crop top features a wide boat neckline and a tight, wrapped crossover hem that completely exposes the entire midriff and navel. Below, tight blue denim mini s with frayed edges sit extremely low on the hips, closely contouring the upper thighs.
Framing: Shot from directly above, looking down at her, framing her head, body, thighs, and knees.
Setting: oatmeal washed-linen bedding filling the frame beneath her, a chunky cream knit gathered at one side, the natural weave catching fine shadow.
Style: (a low-resolution late-night snapchat photo:2.5) with heavy compression artifacts, chunky grain and a touch of (handshake blur:1.6) and (harsh flash:2.0). (fast falloff:2.0), the background collapsing into grainy near-black.
(ai generated:-2.0) (anime, cartoon, illustration, pixar:-1.5) (smooth skin:-2)  (detailed skin texture:2.0) (two women:-3.0)  
 AltGirl
A
""";

var path = @"D:\smsrc\StabilityMatrix.Avalonia\Assets\ImagePrompt.tmLanguage.json";
await using var stream = File.OpenRead(path);
var registry = new Registry(new RegistryOptions(ThemeName.DarkPlus));
var grammar = LoadGrammarFromStream(registry, stream);
var result = grammar.TokenizeLine(prompt);
var bad = 0;
foreach (var t in result.Tokens)
{
    if (!t.Scopes.Any(s => s.Contains("invalid.illegal")))
        continue;
    bad++;
    var end = Math.Min(t.EndIndex, prompt.Length);
    var text = prompt[t.StartIndex..end].Replace("\r", "\\r").Replace("\n", "\\n");
    Console.WriteLine($"BAD [{t.StartIndex}-{end}] '{text}' :: {string.Join(", ", t.Scopes)}");
}
Console.WriteLine(bad == 0 ? "ALL OK" : $"BAD COUNT={bad}");

static IGrammar LoadGrammarFromStream(Registry registry, Stream stream)
{
    IRawGrammar rawGrammar;
    using (var sr = new StreamReader(stream))
        rawGrammar = GrammarReader.ReadGrammarSync(sr);

    var locatorField = typeof(Registry).GetField("locator", BindingFlags.NonPublic | BindingFlags.Instance);
    var locator = (IRegistryOptions)locatorField!.GetValue(registry)!;
    var injections = locator.GetInjections(rawGrammar.GetScopeName());
    var syncRegistryField = typeof(Registry).GetField(
        "syncRegistry",
        BindingFlags.NonPublic | BindingFlags.Instance
    );
    var syncRegistry = (SyncRegistry)syncRegistryField!.GetValue(registry)!;
    syncRegistry.AddGrammar(rawGrammar, injections);
    return registry.GrammarForScopeName(rawGrammar.GetScopeName(), 0, new Dictionary<string, int>())!;
}
