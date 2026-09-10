# Switch and keycap catalog imports

The catalogs are offline JSON data maintained with a manual import-and-review process. This tooling does not change the app, database, device selections, installer, or release workflow.

## Files

| Path | Purpose |
|---|---|
| `Assets/Catalogs/switches.json` | Accepted switch names and optional metadata |
| `Assets/Catalogs/keycaps.json` | Accepted keycap set, add-on, and artisan names with optional metadata |
| `Scripts/Catalogs/sources.json` | Run and per-source fetch dates, Matrix commit, stable upstream identities, canonical IDs, source links, last observed fields, and collection variant notes |
| `Scripts/Catalogs/overrides.json` | Reviewed duplicate decisions, metadata corrections, and exclusions |
| `Scripts/Catalogs/aliases.json` | Shared company spellings, case-sensitive abbreviations, profile aliases, and explicit OEM relationships |
| `Scripts/Catalogs/Aliases.ps1` | Applies those rules to display fields and duplicate comparisons while preserving IDs and source observations |
| `Scripts/Import-Catalogs.ps1` | Fetch, replay, validate, and promote entry point |
| `artifacts/catalog-import/<run>/` | Ignored source snapshots, candidate files, and review report |

Requires Windows PowerShell 5.1 or later, with permission to run local scripts. No modules, packages, database, or API credentials are required. Only `Fetch` accesses the network.

## Catalog fields

Both catalogs contain `schemaVersion`, `catalogVersion`, `totalCount`, and an `entries` array. The importer calculates `totalCount` from the entries; validation rejects missing or mismatched counts. Each entry requires a stable `id` and `name`.

| Optional field | Catalog | Meaning |
|---|---|---|
| `manufacturer` | Both | Explicitly identified manufacturer |
| `brand` | Both | Selling or commissioning brand. Recorded even when it matches the manufacturer, so a missing brand always means the seller is unknown rather than the same company |
| `designer` | Both | Credited designer or collaboration |
| `switchType` | Switches | `linear`, `tactile`, or `clicky` |
| `profile` | Keycaps | Explicitly documented profile |
| `material` | Keycaps | Explicitly documented material |

- Unknown values are omitted. Retailer names are never treated as manufacturers.
- Source labels supply metadata; comparisons and marketing prose are not used to infer specifications.
- Names retain meaningful revisions, rounds, and switch weights/colors. Independently listed keycap add-ons, kits, and artisans have their own entries; kit options within a parent product remain attached to that product.
- Accepted IDs remain fixed through name changes. Alternate names remain traceable in source observations and reviewed bindings.
- Catalog versions increase only when accepted entry content changes; source fetch timestamps do not cause catalog-version changes.

## Update step by step

Run commands from the repository root. Choose a new run directory for each live fetch.

1. Fetch all nine sources and generate staged candidates:

   ```powershell
   .\Scripts\Import-Catalogs.ps1 -Action Fetch -Run artifacts/catalog-import/2026-09-09
   ```

2. Read `<run>/report.json` and the JSON files under `<run>/candidate/`. The report lists additions, changes, explicit removals, possible duplicates, metadata conflicts, missing listings, rejected items, and warnings about omitted source values. Duplicate/conflict reports deliberately return a failure exit status until resolved. Check collection `notes` in the candidate source mappings before merging similarly named specimens.
3. Edit `Scripts/Catalogs/overrides.json` to resolve product decisions, or `Scripts/Catalogs/aliases.json` for shared naming rules, then replay the same cached inputs:

   ```powershell
   .\Scripts\Import-Catalogs.ps1 -Action Replay -Run artifacts/catalog-import/2026-09-09
   ```

4. Run the offline tests and inspect the candidate diff:

   ```powershell
   .\Scripts\Catalogs\Tests\Test-Catalogs.ps1
   git diff --no-index -- Assets/Catalogs artifacts/catalog-import/2026-09-09/candidate/Assets/Catalogs
   ```

   `git diff --no-index` returns exit code 1 when differences exist. On the first import, inspect the candidate files directly because the accepted catalog directory may not exist yet.

5. Promote the reviewed candidate and validate the accepted files:

   ```powershell
   .\Scripts\Import-Catalogs.ps1 -Action Promote -Run artifacts/catalog-import/2026-09-09
   .\Scripts\Import-Catalogs.ps1 -Action Validate
   git diff -- Assets/Catalogs Scripts/Catalogs/sources.json Scripts/Catalogs/overrides.json Scripts/Catalogs/aliases.json
   ```

6. Commit both catalogs, source mappings, overrides, and alias rules together. The tool never commits, tags, or publishes. For a later rollback, revert that catalog commit as a unit.

`Fetch` downloads once and writes `fetch.json` only after every source completes. `Replay` verifies cached file hashes and pagination without network access. Failed fetches remain inspectable but cannot be replayed; retry with a new run directory.

Older snapshots missing any of the nine sources cannot be replayed by this importer. Adding an adapter therefore invalidates every earlier snapshot. Rather than re-downloading everything, seed a new run from an existing one:

```powershell
.\Scripts\Import-Catalogs.ps1 -Action Fetch -Run artifacts/catalog-import/2026-09-10 -Reuse artifacts/catalog-import/2026-09-09
```

`-Reuse` verifies every recorded checksum in the older run, copies those files into the new run, verifies them again after copying, and downloads only the sources the older snapshot lacks. Each carried source keeps its original `fetchedAt` and records a `reusedFrom` folder name, so `sourceFetchDates` still reports each source's actual retrieval date. Sources no longer registered are dropped and named. `-Reuse` still refuses to write into an existing run directory, so a snapshot is never overwritten. The KeycapLendar expansion predates this option and did the same thing by hand under `artifacts/catalog-import/keycaplendar-2026-09-09/`.

Import metadata now lives beside the scripts in `Scripts/Catalogs/`. After this folder move, run `Replay` on an existing complete snapshot before promoting it; replay writes candidate mappings and receipts with the new paths. Raw snapshot folders under `artifacts/catalog-import/` keep their original layout.

`Promote` requires a clean report and matching input, candidate, and accepted-file hashes. Editing overrides, aliases, or accepted files requires a new replay. A failed replay invalidates its earlier promotion receipt. Ordinary write failures roll back files already replaced; replacement of the three files is not a filesystem-wide transaction, so a process kill during promotion may require restoring all three from Git and replaying. Keep this maintainer operation serialized.

## Alias rules

- `aliases.json` is maintained in Git beside the importer. `canonical` is the full company spelling used in manufacturer, brand, and designer fields; `aliases` match without regard to case. `exactAliases` match only the listed case: `dk` means Divinikey, while `DK` means Dangkeebs. Unlisted `Dk` is left unchanged.
- `namePrefix` selects an abbreviated prefix for product names. `namePrefixes` can override it by catalog: KeebsForAll uses `KFA` for switches and `kfaPBT` for keycaps. Both must resolve to the same company through the alias map. Without a preference, product names use the canonical spelling.
- Product names prefer established abbreviations such as KKB, ePBT, MW, KBS, GOM, NK, SWG, WS, RAMA, PrimeKB, RK, DMK, JCS, and C3. Company metadata keeps its full spelling, and comparisons use the full identity regardless of the display preference.
- Rules standardize manufacturer/brand fields, whole company names in designer credits, leading company names in product titles, and explicit `x` collaborations. Model words, weights, revisions, rounds, collection numbers, and designer punctuation remain intact.
- Alias matches never merge products automatically. Existing IDs remain fixed; new matching source listings still require reviewed bindings. Original source spellings stay in `sources.json`, and collection identity digests are independent of editable aliases.
- TX and Typeplus remain separate. Glove/Glove.Studio/Glove Studio share one company spelling, as do KFA/kfaPBT/KeebsForAll. Combined manufacturer labels retain their named parties.
- Durock and JWICK remain separate brands, with JWK recorded as their OEM. Explicit selling-brand values take precedence when a source uses a brand as its manufacturer label. The legacy combined `Durock/JWK` label establishes JWK as manufacturer without assigning a selling brand.
- CYL maps to the Cherry profile. An explicit `GMK CYL` or `GMK MTNU` title supplies that profile when it is otherwise absent; the title qualifier remains, and MTNU stays separate.
- Alias edits require `Replay` before `Promote`; the candidate receipt checks the alias file hash. Alias changes and resulting catalog changes should be reviewed and committed together.

## Keycap fields a name already states

Some keycap fields are stated by the set name itself. These rules fill those fields and nothing else. They run once on the merged entry, after every source has contributed, so any source value wins and a `null` entry override still removes what they add. Switches are untouched, where a leading company names the seller rather than the maker.

- A name beginning with a shape label listed under `profiles` in `aliases.json` supplies that profile. `CYL` resolves to Cherry. The recognized shapes are Cherry, CYL, MTNU, SA, DSA, DSS, DCS, KAT, KAM, KSA, XDA, MDA, MT3, and OEM.
- A company declares what it determines through `keycapDefaults` on its `aliases.json` entity. `manufacturer` means a set led by that company was made by it. `profile` and `material` give its usual shape and plastic. `unlessProfile` names a shape that withdraws both, because that line differs.
- Only GMK and KeyKobo declare these. GMK supplies manufacturer GMK, Cherry profile, and ABS, unless the name contains MTNU. KeyKobo supplies manufacturer KeyKobo and ABS. Both were confirmed against the accepted catalog with no counterexample: 590 of 590 observed GMK manufacturers, 61 of 63 observed GMK materials, and the two exceptions are the MTNU sets. KeyKobo was 27 of 27 and 26 of 26.
- Selling brands are deliberately excluded. NovelKeys, Milkyway, Swagkeys, Wuque Studio, and PBTfans lead many set names without making them, so they declare nothing. PBTfans also has a reviewed decision omitting a disputed manufacturer claim.
- A declared profile or material must already be recognized, and a declared manufacturer must be the entity's own canonical spelling. Unknown fields, unknown shapes, and unsupported plastics fail when the alias map loads.

## Review decisions

The override document has three maps:

```json
{
  "schemaVersion": 1,
  "bindings": {
    "kbdfans:12345": "gmk-example-r2"
  },
  "entries": {
    "keycaps/gmk-example-r2": {
      "name": "GMK Example R2",
      "designer": "Credited Designer"
    }
  },
  "excluded": {
    "divinikey:67890": "Non-keycap accessory"
  }
}
```

- The example IDs are illustrative. Use the exact `source` keys and proposed IDs from your report.
- `bindings` maps an upstream identity to the canonical ID. Confirm matching rounds/variants before merging; explicitly assign distinct IDs when similarly named products differ.
- Reassigning every source binding away from an accepted duplicate retires that duplicate and records the removal in the report. Keep all original source bindings on the surviving ID and move/remove any overrides for the retired ID. An entry with another remaining source binding is retained.
- Keep ambiguous variants separate. Collection numbers can distinguish specimens when the source does not identify their revision; a collection-number suffix is an import label, not an official product revision.
- `entries` pins reviewed metadata for a canonical ID. Set an optional field to `null` to omit a value that cannot be established reliably. Do not change `id` here.
- `excluded` records a reason for rejecting a source listing. An explicit exclusion can correct a mistakenly accepted entry: its mapping is removed, and the entry is removed only if no other source mapping remains. Remove any entry override for that ID too. These corrections appear in `removals`; source disappearance and new automatic filters never delete accepted history. Stock availability is never an exclusion reason.
- Source omissions retain accepted records and metadata. A drop below half the prior mapped count (for sources with at least 20 bindings) aborts the import for investigation.
- Existing metadata is retained when a source stops supplying it. A deliberate correction/removal belongs in `entries`.
- Unrecognized multi-option switch listings fail for review instead of silently discarding variants. Add an adapter rule with a fixture test when a source introduces a new option format.

## Sources and limitations

Links below point directly to data feeds, file indexes, or downloadable files where available. Shopify links start at page 1; increment `page` until an empty `products` array is returned. Embedded-data pages and JavaScript bundles are labeled explicitly because they are not standalone JSON endpoints.

| Source | Coverage | Source format |
|---|---|---|
| [SwitchOddities JSON](https://switchoddities.com/collections/switch-samples/products.json?limit=250&page=1) | Published switch sample listings, including some nonstandard and mouse switches | Public product JSON, paginated to an empty terminal page |
| [ThereminGoat collection: XLSX download](https://drive.google.com/uc?export=download&id=1lEsJaTX4nwtxcx2WL1EcwuwokWyqnuDv) | Broad switch collection, including historical models, prototypes, and variants | Public workbook; literal name, type, manufacturer, and variant-note cells |
| [ThereminGoat switch scores: raw CSV](https://raw.githubusercontent.com/ThereminGoat/switch-scores/refs/heads/master/1-Composite%20Overall%20Total%20Score%20Sheet.csv) | Scored switches with explicit manufacturer and type columns | Public CSV export of a spreadsheet; six stacked ranking tables and a manufacturer table beside the first one |
| [UniKeys JSON](https://unikeyboards.com/collections/keyboard-switches/products.json?limit=250&page=1) | Additional named switches and explicit manufacturer/designer/type metadata | Public product JSON; named weights and modifications are retained, packaging quantities are removed |
| [Matrix / Matrix Zou and contributors: JSON file index](https://api.github.com/repos/matrixzj/matrixzj.github.io/contents/docs/gmk-keycaps?ref=c3b59ab9c059c5976b5ecc0789f6df97960614c6) | Historical GMK sets and add-ons; navigation and color-reference pages are filtered | JSON directory listing with `download_url` links to individual GMK Markdown documents at the imported commit; not a combined catalog JSON |
| [Divinikey JSON](https://divinikey.com/collections/keycap-sets/products.json?limit=250&page=1) | Keycap sets, add-ons, and artisans exposed by its collection | Public product JSON |
| [KBDfans JSON](https://kbdfans.com/collections/keycaps/products.json?limit=250&page=1) | Keycap sets, add-ons, and artisans exposed by its collection | Public product JSON |
| [DCS Wiki / Myth: data bundle](https://dcs.wiki/_next/static/chunks/911-9f506cad5fbc6f41.js) | Historical and current DCS sets and individual add-on kits | JavaScript bundle containing the JSON array, verified on 2026-09-09; bundle URLs can change after site updates |
| [KeycapLendar / EskiMojo14: Firestore JSON](https://firestore.googleapis.com/v1/projects/keycaplendar/databases/%28default%29/documents/keysets?pageSize=300) | Past, current, and upcoming named sets and add-ons, including canceled designs | Public website collection; follow `nextPageToken` until absent. The adapter requests only catalog fields. |

Coverage is not exhaustive. Retailer collections can remove discontinued products, Matrix's visible GMK index ends at 2024, and DCS Wiki can omit smaller or private runs. KeycapLendar also includes interest checks and canceled designs. Reviewed canceled listings are excluded from the accepted catalog; a remaining listing still does not establish that a product shipped. Other manufacturer-specific catalogs discussed during planning are not additional adapters in this implementation. Blank metadata is intentional; manual corrections can add documented values.

- KeycapLendar's document ID provides stable identity; its alias provides a share link. Names combine the source category and colorway, preserving round, add-on, and cancellation labels. Designer arrays become one credit string; source dates and project links remain in provenance notes.
- Its `profile` field mixes manufacturers/brands with actual profiles. Only recognized shape labels and explicit material suffixes are extracted. The adapter does not turn `GMK`, `PBTfans`, or unfamiliar categories into profiles, or infer manufacturer/brand values from them.
- Fetch and replay verify the complete token chain, source endpoint, document IDs, typed fields, and hashes. Repeated IDs/tokens, missing pages, unexpected pages after completion, and empty or malformed imports fail for review. No images, editor identities, or user collections are requested.
- This adapter uses the publicly readable collection behind the website, verified on 2026-09-09. The separate [supported API](https://github.com/EskiMojo14/keycaplendar#api) requires an API key and secret arranged with the maintainer. If public collection access changes, fetching fails; the importer does not attempt authenticated access. The repository's code license is not treated as a separate license for the catalog compilation.

- DCS Wiki has no dedicated JSON endpoint in this adapter. Each fetch discovers current same-origin script URLs from the [catalog page](https://dcs.wiki/keycaps), locates one static `JSON.parse` array, and decodes the string without executing JavaScript.
- Its page and data bundle are cached with hashes. Offline replay verifies that the bundle belongs to that page and that every record has a matching detail link. Missing arrays, changed fields, duplicate source IDs, or page/bundle mismatches stop the import for review.
- Only explicit name, manufacturer, designer, profile, and material values are imported. Cherry/Gorton legend styles are not keycap profiles. Unknown designer credits are omitted.
- All structured DCS Wiki records are included, covering full sets and individual add-ons such as accent kits, spacebars, and modifier kits. General showcase compilations outside that array are not separate product records. Separate releases retain distinct identities.

### Switch source parsing

- ThereminGoat's workbook is read with native .NET ZIP/XML support; Excel and spreadsheet packages are not required. The adapter checks the worksheet relationship, headers, paired rows, shared strings, and unique source identities. Formulas in import cells, XML external entities, malformed workbooks, and changed schemas stop the import.
- The 2026-09-09 workbook contains 4,239 named records; its summary counter says 4,241. Skipped numbering and one duplicate number mean the counter is not the import count. Empty trailing template rows are ignored. Two stabilizer records are excluded, leaving 4,237 mapped switch records.
- Source identities use collection numbers. Repeated number 3215 is disambiguated with a deterministic name digest, preserving both Keygeek Jasmine Milk and Keygeek Raw specimens. A later upstream renumbering needs a reviewed binding decision.
- ThereminGoat's score sheet is a separate adapter reading the same maintainer's published CSV. The 2026-09-09 file holds six stacked ranking tables. The first is the composite list of 463 scored switches, and the other five repeat those same switches split by type, so only the first is imported. A manufacturer ranking occupies the columns beside it and is never read.
- Ranks change between publications, so score identities are a digest of the reviewed name rather than a rank. The composite table ends at its `AVERAGE OF ALL` row. A renamed column, an unexpected summary label, or an unranked row stops the import.
- The sheet lists one switch twice when it was scored on two dates. Repeated rows that agree are collapsed with a warning, and repeated rows that disagree stop for review. `Unknown` and question-marked manufacturers are omitted and reported, matching the workbook adapter. The 462 imported records supply 437 manufacturers and 462 types.
- `Silent Linear` and `Silent Tactile` map to the corresponding switch types. Unknown manufacturers, question-mark qualifications, and unsupported or contradictory type labels are omitted and reported. There is no manufacturer inference from product-name prefixes.
- Collection notes remain in source mappings. Reviewed KS-series, housing, mold, nameplate, and ChinaJoy variants receive distinct names/IDs; repeated specimens with uncertain differences stay separate using collection-number labels.
- UniKeys' 178 listings produce 285 mapped switch/variant records after excluding its tester. Packaging-only options share the product identity. Weight or modification options use upstream variant IDs; matching pack-size variants can share a reviewed canonical ID while retaining both bindings.
- UniKeys adds missing coverage and metadata through reviewed matches. Matching ignores only reviewed title decorations and packaging details; weights, spring lengths, revisions, silent variants, and modifications remain meaningful. New option formats stop for review.

### Additional catalog research

Checked on 2026-09-09. The sources below remain references or candidates; the nine active adapters are listed above. Listing counts can include variants, duplicates, or accessories.

| Source | Data found | Use and remaining work |
|---|---|---|
| [Cherry XTRFY: HTML containing JSON](https://cherryxtrfy.com/keyboard-switches) | 89 switch-category products in JSON split across hidden `bootup-data` inputs; no standalone JSON endpoint verified | Explicit switch-type specifications for Cherry, Gateron, and Kailh. Concatenate and decode the input values, then parse JSON without executing page scripts. Do not assign Cherry as manufacturer to the entire collection. |
| [Gateron store JSON](https://www.gateron.co/products.json?limit=250&page=1) | Public Shopify feed: 35 products, followed by an empty second page | Useful product families and variants. Some type/specification tables appear only on the product pages, outside the feed's description. The [Gateron specification index](https://www.gateron.com/pages/product-specification) is an HTML reference, not a JSON feed. |
| [Milktooth: HTML containing JSON](https://milktooth.com/) | Embedded React page data with switch IDs, names, brands, and types; no standalone JSON endpoint verified | Homepage arrays are partial selections; a complete switch listing feed has not been verified. Brand must remain separate from manufacturer. |
| [KeebFinder: HTML containing JSON](https://keeb-finder.com/switches) | Embedded React page data with 48 initial switch products, including brand and `actuationType` metadata; no standalone JSON endpoint verified | Initial page data is not the entire advertised catalog. Verify pagination and follow specification links for manufacturer claims. |
| [KBD.news: HTML table](https://kbd.news/switch/) | HTML switch database listing 500 models with structured specification columns | Useful source-backed type cross-checks; no complete JSON feed found. |
| [Keygeek: HTML listing](https://www.keygeek.cn/products_16/) | Manufacturer's custom-switch listing and linked product pages | Direct manufacturer reference; no JSON feed verified. UniKeys currently offers the simpler structured import source. |

- Match the exact model, revision, and weight before sharing an existing ID. For example, MX2A, Hyperglide, and vintage Cherry listings must not be merged merely because their colors match.
- Extract an explicit type from a specification label or product title. Review prose can mention competing switches, so searching the entire description for `linear` or `tactile` is unsafe. `Silent` and `Hall effect` alone do not establish the current catalog's linear/tactile/clicky classification.
- Manufacturer inference from name prefixes remains disabled. Source-backed corrections belong in overrides.
- Research responses are cached locally under `artifacts/catalog-import/source-research-2026-09-09/`. This research cache is not a complete importer fetch and cannot be promoted.

### Source overlap review

Compared the cached sources on 2026-09-09. **No source was established as redundant, so none was removed.** This historical comparison used the 2,274-switch catalog before the expansion. ThereminGoat and UniKeys now supplement SwitchOddities; the four keycap adapters used in that comparison remain active alongside KeycapLendar.

| Compared source | Records checked | One-to-one name matches in ThereminGoat's collection | Name-match rate |
|---|---:|---:|---:|
| SwitchOddities accepted catalog | 2,274 | 1,009 | 44.4% |
| ThereminGoat score CSV | 463 | 382 | 82.5% |
| UniKeys product listings | 178 | 63 | 35.4% |
| Cherry XTRFY product listings | 89 | 35 | 39.3% |
| KBD.news parsed table | 575 | 154 | 26.8% |
| Keebgod parsed table | 564 | 83 | 14.7% |
| KeebFinder cached sample | 46 distinct product IDs | 10 | 21.7% |
| Milktooth cached sample | 65 distinct switch IDs | 16 | 24.6% |

- These are name-match rates, not final catalog coverage. Case, punctuation, and retailer title decorations are normalized. Revisions and variant details are preserved, and names matching multiple collection rows are excluded from this table. Metadata can still conflict even when names match.
- Alias and variant review can increase overlap. For example, the collection lists `RAMA WORKS Duck` where SwitchOddities uses `Rama Duck`, and separates Keygeek Y2 spring lengths/weights where UniKeys groups them. These differences do not establish that an unmatched switch is absent.
- Grouped Gateron store listings need explicit color/weight/pin mappings before a coverage percentage is meaningful. KeebFinder and Milktooth samples do not establish their full catalog coverage. Keygeek's manufacturer pages remain specification references, without a verified complete catalog extraction.
- Some listings remain unmatched after targeted name checks, including UniKeys' HMX Rain Rail and Keygeek Y2X, and SwitchOddities' Drinkey Xuan Tie Magnetic. Retain their source coverage rather than merging them into similarly named switches.
- Source disagreements discovered in this comparison are handled in the switch import decisions below.
- Detailed matches, unresolved variants, field differences, and input hashes are saved in `artifacts/catalog-import/source-research-2026-09-09/switch-coverage-report.json`. Similar-name suggestions are separate in `switch-coverage-suggestions.json`; none were applied to the accepted catalogs.

Source notices checked on 2026-09-09:

- [Matrix's license](https://github.com/matrixzj/matrixzj.github.io/blob/master/LICENSE.txt) is CC BY-NC-ND 4.0. Its public repository is not an unrestricted redistribution license. The import extracts factual names/credits and changes their organization; it does not reproduce pictures or descriptive prose. Preserve attribution and assess permission before distributing an adapted compilation.
- [Divinikey's terms](https://divinikey.com/policies/terms-of-service) and [KBDfans' terms](https://kbdfans.com/policies/terms-of-service) include restrictions on crawling/scraping. A reachable JSON endpoint does not itself grant bulk-access or redistribution permission.
- [SwitchOddities' terms](https://switchoddities.com/policies/terms-of-service) should also be checked for the intended use. No general open-data license is assumed for retailer catalogs.
- [DCS Wiki's notice](https://dcs.wiki/about) credits Signature Plastics and the respective makers/designers, describes an informational, noncommercial archive, and supplies no unrestricted data-reuse license. Preserve its attribution; only factual fields are extracted here.
- Raw responses remain local under ignored `artifacts/`. Catalog data and upstream material are not represented as newly licensed under the application's code license. Public distribution and automatic fetching by end users are outside this implementation.

The fetch manifest records retrieval timestamps, URLs, content hashes, and the Matrix commit. Keep that run folder when auditing the initial import or a later update. The tests use small synthetic fixtures and do not need any live source access.

## Initial review decisions

- Matching retailer listings for the same named PBTfans/Keykobo/GMK releases share IDs. Explicit round suffixes remain distinct.
- Olivia and Olivia++ retain separate identities. Plus signs are meaningful in name matching and ID generation.
- Matrix's 2020 Hammerhead/Noire and 2022 Classic Beige records remain separate from the newer retailer listings; source years distinguish otherwise repeated names without inventing an official round number.
- Two KTT Matcha nameplate versions share the named model and matching specifications. Durock Ice King and TTC Speed Silver duplicate listings also share model IDs.
- KTT Phalaenopsis long-stem, original Skyloong Chocolate Rose, and Outemu Brown with different documented actuation weights remain distinct.
- One uncertain Feker Matcha revision and an unidentified Outemu specimen from a mixed bag are explicitly excluded until their identities can be established.
- DCS Wiki confirms Signature Plastics as Mudbeam's manufacturer, correcting the retailer's DCS label. Six DCS Wiki records share existing retailer IDs; Molch remains available from Divinikey.
- Add-ons are included across all keycap sources. DCS Wiki's Row 4 Accent Kits and Divinikey's R4 Accent listing share one entry, preserving both source names in provenance.
- The existing `dcs-dolch` ID belongs to Carter's 2026 release. The 2024 private rerun has `dcs-dolch-2024`; both display their years. Richat retains its existing ID and retailer's `mori` designer credit, with DCS Wiki's `morimx` attribution preserved in source observations.
- Conflicting KBDfans/PBTfans manufacturer claims for Thermal are omitted while retaining the PBTfans brand. Minor Keykobo/Signature Plastics spelling differences are resolved in overrides.

## Switch expansion decisions — 2026-09-09

- ThereminGoat and UniKeys bring the switch catalog to 5,676 entries, catalog version 3. The 936 keycap entries remain at version 3. The import adds 3,404 switch entries and removes two mistakenly included accessories; existing switch IDs otherwise remain intact.
- Existing switches gain 735 manufacturer values, 890 type values, and two designer credits. UniKeys contributes 181 additional entries; 104 of its variant records share reviewed IDs from other sources. These counts describe this snapshot, not exhaustive coverage.
- Ambiguous specimens stay separate, including KTT Phalaenopsis, Skyloong Chocolate Rose, Hi-Tek collection 473, and repeated collection names without enough variant detail. Linear/tactile Lilac and Wingtree Matcha Cream releases have distinct IDs. Generic retailer listings are not assumed to match a specific KS-series, housing, or mold variant.
- [Tecsee's Purple Panda specification](https://www.tecseekeys.com/products/tecsee-purple-panda-keyboard-switch-pme-material-tactile-pom-stem-68g-spring-mx-switches.html) confirms tactile; [Tecsee's Carrot specification](https://www.tecseekeys.com/products/tecsee-carrot-pme-linear-pom-stem-switch-for-5pin-rgb-smd-mechanical-gaming.html) confirms linear. These correct conflicting workbook type cells.
- [ThereminGoat's Mode review](https://www.theremingoat.com/blog/mode-tomorrow-switch-review) confirms Signal is tactile and Reflex is linear. [CannonKeys' catalog](https://cannonkeys.com/collections/best-selling-products/switch) lists both Lilac types; UniKeys lists both Matcha Cream types.
- WS Aurora retains the retailer's Haimu attribution, supported by its [manufacturer comparison](https://switchoddities.com/blogs/odd-blog/haimu). Keyspensory Haze uses Aflion, supported by [Keyspensory's listing](https://keyspensory.store/products/extra-clearence). Explicit Durock/JWK, Kaihua/Kailh, and Gateron EF labels are normalized in reviewed overrides.
- Disputed manufacturers remain blank for XCJZ Jerrzi Lotus Stem and the two matched Akko models. ThereminGoat's [Lotus Stem review](https://www.theremingoat.com/blog/xcjz-jerrzi-lotus-stem-switch-review) describes the Huano/Jerrzi factory connection as conjecture. UniKeys' six `MDD` manufacturer labels also remain omitted; its explicit MMD brand attribution is retained.
- Source observations preserve original spellings and conflicting values. Reviewed corrections remove explanatory phrases from manufacturer/designer values and omit the `Store Link` designer placeholder.
- Exclusions: ThereminGoat's two stabilizers, UniKeys' tester, and the accepted SwitchOddities custom-price placeholder and switch holder. Other prior exclusions remain in force.
- Verification: 25 offline test groups cover source parsing, variant separation, preserved notes, checksums, replay, and promotion. The reviewed seven-source import and a repeat replay have no unresolved duplicate or metadata conflicts.

## KeycapLendar expansion — 2026-09-09

- This expansion represented all 2,255 source listings: 567 shared existing catalog IDs, and the remainder added 1,686 entries after two repeated projects were consolidated. It produced 2,622 keycap entries at version 4. The switch catalog remained unchanged at 5,676 entries, version 3.
- The import fills 48 previously missing designer credits, one profile, and four material values on existing entries. Existing names and metadata are preserved, including disputed designer credits; each KeycapLendar attribution remains available in its source observation.
- GMK ADA, Cosmos, and Stonks, DSA Vegas Nights, the earlier DCS Pink Alert interest check, and paulgali's GMK TA Neo retain separate identities where project references, years, or designers differ. Year/designer suffixes distinguish these entries without inventing round numbers.
- Repeated Guide Line, Vaporwave R2, and MAGA listings share reviewed IDs based on matching product links or project references. [War Maiden's designer thread](https://geekhack.org/index.php?topic=122730.0) connects its interest check to the December 9 group buy and confirms Keyreative, Cherry profile, and PBT; both KeycapLendar records share one entry.
- Accented spellings and category labels such as `PBTfans ABS` share IDs with matching existing releases. The Purpolch listing's erroneous Thermal link does not trigger a merge. Colorways beginning with CJK characters retain their full names when checking for duplicates.
- The expansion snapshot includes ignored baseline copies and a detailed review audit under `artifacts/catalog-import/keycaplendar-2026-09-09/`. Accepted mappings and decisions remain in `Scripts/Catalogs/sources.json` and `overrides.json`.
- Verification: 29 offline test groups cover typed Firestore data, complete pagination, malformed/repeated pages, stable IDs, variant separation, replay, and promotion.

## Catalog cleanup — 2026-09-09

- The emoji-named GMK entry is explicitly excluded by its KeycapLendar source ID, so later imports do not restore it.
- Reviewed overrides remove 24 trademark symbols from PBTfans brand values. Brand fields that duplicate the manufacturer are omitted; the source observations retain the original spelling for attribution.
- The keycap catalog contains 2,621 entries at version 5. The switch catalog is unchanged.

## Alias standardization — 2026-09-09

- The shared alias map contains 92 company/qualified-label groups, the CYL-to-Cherry profile rule, and JWK's explicit relationships to Durock and JWICK. It applies the maintainer-confirmed abbreviations and spelling corrections.
- Nineteen confirmed spelling-error duplicate pairs share reviewed IDs and retain both original source records. Other alias-only matches and ambiguous variants remain separate.
- Fifteen switches with question marks indicating uncertain identity are explicitly excluded through their 20 source bindings. Literal punctuation in `GMK Why?` and the `Hello? X Rubrehose` designer credit is retained.
- The switch catalog contains 5,642 entries at version 4; the keycap catalog retains 2,621 entries at version 6. Naming, company roles, and profile normalization update 869 surviving switch entries and 415 keycap entries.
- Verification: 36 offline test groups pass. The complete cached replay has no unresolved duplicates or metadata conflicts; the review also checks retained variant IDs, source spellings/notes, fetch dates, explicit omissions, counts, and designer punctuation.
- The ignored `artifacts/catalog-import/alias-cleanup-2026-09-09/` snapshot contains the baseline, duplicate decisions, exclusions, candidate report, and verification results.

## Canceled-set cleanup — 2026-09-09

- Removed all 82 keycap entries labeled `CANCELED`, with persistent exclusions for their KeycapLendar source IDs.
- The keycap catalog contains 2,539 entries at version 7. The switch catalog remains unchanged at 5,642 entries, version 4.
- The ignored `artifacts/catalog-import/canceled-cleanup-2026-09-09/` snapshot preserves the removed names, baseline, source records, and candidate report.

## Product-name abbreviations — 2026-09-09

- Seventeen company groups now prefer abbreviated product-name prefixes, including `KFA` for switches and `kfaPBT` for keycaps. Manufacturer, brand, and designer fields retain their canonical company spellings.
- Updated 270 switch names and 369 keycap names. Two reviewed title overrides prevent repeated `NK NK` and `WS WS` prefixes; their product IDs remain separate.
- Catalog counts remain 5,642 switches and 2,539 keycaps, at versions 5 and 8 respectively. All IDs, other metadata, and source records are unchanged.
- All 37 offline test groups pass, and the full cached replay has no unresolved duplicates or metadata conflicts. The ignored `artifacts/catalog-import/name-prefixes-2026-09-09/` snapshot contains the baseline and verification results.

## Score sheet and keycap inference — 2026-09-09

- ThereminGoat's score CSV is the ninth adapter. Its 462 imported records add 76 switch entries and bind 386 to existing ones. The switch catalog is 5,713 entries at version 6. Existing switch entries gain 7 manufacturers and 4 types, and the source supplies 437 manufacturers and 462 types overall.
- Its 40 `Durock/JWK` manufacturer labels resolve through the existing `manufacturerLabels` rule, so the import raised no metadata conflict.
- Keycap inference fills 389 manufacturers, 870 profiles, and 917 materials. Every inferred value was GMK or KeyKobo for manufacturer, Cherry for profile, and ABS for material. The keycap catalog stays at 2,539 entries and moves from version 8 to 9.
- Eight GMK and KeyKobo entries previously recorded the maker in `brand`. Gaining an explicit manufacturer promoted those to `manufacturer` under the existing rule that drops a brand matching its manufacturer.
- The shape-label rule changed nothing in this snapshot. Every entry led by a recognized shape already carried that profile, so the rule guards future imports rather than filling a present gap.
- Five confirmed spelling-error duplicate pairs surfaced when the score sheet matched both halves. RAMA Duck, WS Light Tactile, WS Morandi, WS Onion, and Moyu Studio x XCJZ Snow Grape each existed twice after alias standardization unified their prefixes. Each pair now shares the ID whose slug matches the accepted display name, both source records are retained, and the reviewed JWK manufacturer override moved to the surviving `ws-onion`.
- `Keebz N Cables x HMX Ice Cendol` keeps its established capitalization through a reviewed name override. The score sheet spells it `Keebz n Cables`, and case-only differences collapse during the merge, so the display name needed pinning rather than being decided by sort order.
- `-Reuse` seeded this run from `artifacts/catalog-import/name-prefixes-2026-09-09`, carrying eight verified sources including 533 Matrix documents and downloading only the score CSV.
- Verification: 43 offline test groups pass. The complete cached replay has no unresolved duplicates or metadata conflicts, both MTNU sets keep PBT and MTNU, retired IDs are absent, and no selling-brand-led set gained a manufacturer.

## Prototype, sample, and modification cleanup — 2026-09-09

Retail listing is the production test used here. A switch counts as produced when any retailer source lists it. ThereminGoat's collection and score sheet record what a reviewer held, which is not the same thing.

- Removed all 122 prototype entries. Not one was listed by any retailer in any of the nine sources, and 95 have no production counterpart under any name, so none of them shipped.
- Removed all 95 sample entries. 85 were collection specimens such as factory colour samples, nameplate samples, and unnamed test units. The other 10 were sold, mostly by SwitchOddities, which sells single switches as samples, and were removed on review because a sample is not a distinct product.
- Removed 23 aftermarket modification entries, including the broken-in Cherry listings and the lubed, filmed, and spring swapped ones. A modification is not a switch. Each of these had another entry covering the same product.
- Three switches existed in the catalog only as modified specimens. Removing those entries would have deleted a real product, so their modification wording was stripped instead and each kept one clean entry: Cherry MX1A Black, Cherry MX2A Purple, and Gateron Tangerine.
- Retailers sell some switches as a choice between dry and factory lubed. Those are purchase options, not aftermarket work, so 91 lube-marked entries collapsed into 60 clean products. 13 merged into an entry that already existed and 47 became new clean entries. Weights, revisions, pin counts, poles, and colourways were left intact.
- `NK Dry Black`, `NK Dry Red V1`, `NK Dry Yellow V1`, and `NK Dry Black V1` keep their names. `Dry` is NovelKeys' model there, not a lube state, confirmed by SwitchOddities' `novelkeys-nk-dry-black` listing and the absence of any plain `NK Black`.
- `Unlubed Caramel Chocoate` is a source spelling error and merged into Caramel Chocolate.
- The two Gateron Nightingale specimens disagree on switch type, one linear and one tactile, so the merged entry omits the type rather than choosing between them.
- WS Aurora keeps its reviewed Haimu attribution. A retired JWK specimen override was not allowed to displace it.
- The switch catalog is 5,429 entries at version 7. Keycaps are unchanged at 2,539, version 9.
- Verification: 43 offline test groups pass, the cached replay has no duplicates or conflicts, and no entry in the promoted catalog still carries prototype, sample, or modification wording.

## Cherry consolidation — 2026-09-09

- MX1A is Cherry's designation for the Hyperglide generation, so the retailer's MX1A listings describe switches the Hyperglide entries already cover. The `Cherry MX1A Black` entry created during the modification cleanup was retired and its five modified listings are excluded like every other modification.
- Retailer listings that state no pin count add nothing to the pin-specific entries and were removed: `Cherry Hyperglide Black`, `Cherry Hyperglide Brown`, and `Cherry Ergo Clear`. The pin-specific Hyperglide and Ergo Clear entries remain, including the MX2A and RGB variants.
- Nameplates are packaging, not a different switch. The two Cherry MX Hyperglide Black 3-pin specimens differed only by nameplate and now share one entry. The Huano Budapest, KTT Matcha, Tomato Black, and Zaku nameplate specimens were removed because each already has a plain entry. This reverses the earlier decision to give nameplate variants distinct names and IDs.
- The Huano Nameplate Pack is an accessory and was excluded.
- Three switches were listed both with and without `MX` in the name. `Cherry Dark Blue`, `Cherry Hirose Clear`, and `Cherry Jailhouse Blue` are now bound to their `Cherry MX` entries, which keeps each retailer listing visible on the surviving entry rather than discarding it.
- `Cherry Blossom` and `Cherry MX Blossom` remain separate. They share a word but not a switch: the first is made by JWK and the second by Cherry.
- `Cherry Brown`, `Cherry MX Lock (Grey/Black)`, and `Cherry MX2A RGB Purple` state no pin count and were removed for the same reason. Their pin-specific entries remain. A first pass missed these because it compared whole names, and the counterpart carries a pin suffix.
- The switch catalog is 5,413 entries at version 9. Keycaps are unchanged at 2,539, version 9.

## Automatic product rules — 2026-09-09

The removals above were per-listing decisions keyed to upstream IDs. They persist because the importer re-applies `overrides.json` on every run, but they do not cover listings that appear later. These rules move the same judgments into the adapters so a new fetch stays clean without repeating the review.

`Common.ps1` holds them, and every switch adapter calls `Set-CatalogSwitchProduct`.

- `Get-CatalogSpecimenReason` rejects prototype and sample wording. It applies to both catalogs, matching the accessory filter that already rejects testers and deskmats. `proto` matches only as a whole word, so a product such as Protozoa is unaffected.
- `Get-CatalogModificationReason` rejects broken-in, hand-lubed, modded, spring-swapped, and lubed-and-filmed listings. Switches only. Factory-lubed and pre-lubed listings are deliberately not matched, because that is how the switch ships.
- `Get-CatalogLubeFreeName` removes dry, lubed, unlubed, and factory or pre-lubed wording from a switch name so the two purchase options land on one entry. Weights, revisions, pin counts, poles, and colourways are untouched. NovelKeys' Dry line is exempt by name, since the word is the model there.

Order matters inside `Convert-CatalogProduct`: modification wording is read before lube wording is removed, or a hand-lubed listing would look like an ordinary product. The rules run again after a variant name is composed, because retailers put the lube and modification wording in the option label rather than the title. A UniKeys product whose own title is a modification is rejected before its variants are expanded.

The score sheet keeps its identity digest on the published name so existing bindings survive, and compares repeated rows after cleanup so two spellings of one switch still agree.

Applying the rules to the cached snapshot changed one accepted entry, `Akko Wine White (Prelubed)`, which the manual pass had missed because its lube wording carries no word boundary. The switch catalog is 5,413 entries at version 10.

`HMX Snow Crash (Overlubed Batch)` is left alone. An over-lubed factory batch is a production note rather than aftermarket work, and it is the only entry for that switch.

## Selling brand and maker inference — 2026-09-10

Four more rules, each fill-only and each backed by counting agreements and counterexamples in the accepted catalog rather than by outside knowledge.

- **A leading company is the selling brand.** Whoever made a product, the company its name begins with is the one selling it. This fills 876 switch and 360 keycap brands. When the brand turns out to equal the manufacturer it is dropped, as before, so `Gateron Oil King` records one company and `43 Studio Jing` records brand 43 Studio against manufacturer JWK.
- **A declared factory also names the maker.** Thirteen companies carry `switchManufacturer` in `aliases.json`: Gateron, Kailh, Outemu, Jerrzi, SP-Star, Haimu, Aflion, Grain Gold, Keygeek, JWK, Yusya, Cherry, and Wingtree. Together they fill 213 manufacturers. Every one was checked across the catalog: Gateron agreed 274 times with no counterexample, Kailh 213, Outemu 148. Cherry and Wingtree each have one explainable exception, the JWK-made Cherry Blossom and the Wingtree / Lichicx collaboration labels.
- **Sellers are deliberately not factories.** DareU, LEOBOG, Skyloong, Royal Kludge, Feker, and the other brands that outsource are absent from that list, because the catalog records their switches against Huano, SOAI, Haimu, Jerrzi, and TTC. They now get a brand and no invented manufacturer.
- **A profile made by one company states its maker.** DCS supplies Signature Plastics on 134 agreements with no counterexample, and MTNU supplies GMK. MTNU also supplies PBT. A profile now declares `manufacturer` and `material` beside its aliases.
- Maintainer confirmation added three more: DSS is Signature Plastics, and KAT and KAM are Keyreative. Those three had no manufacturer recorded anywhere in the catalog, so the evidence had to come from the maintainer rather than the data. They fill a further 104 values.
- **SA and DSA state nothing.** They are mostly Signature Plastics, but Chinese manufacturers produce them too, so the shape does not establish a maker. They keep their profile and no manufacturer.
- **A set named for the exception profile is that profile.** `GMK JUST MTNU` previously received nothing, because the MTNU exception only withdrew GMK's Cherry and ABS. It now records MTNU and PBT like every other MTNU set.

Two false positives were found and fixed while reviewing the candidate diff.

- On a keycap set a leading shape word names the profile, not the seller. Cherry is both a profile and a company, and without this 72 Cherry-profile sets would have been recorded as sold by Cherry.
- `Cherry Blossom` is named for the flower and made by JWK, so a reviewed override removes the brand the prefix would otherwise supply. It stays separate from `Cherry MX Blossom`.

Coverage after these rules: switch manufacturer 69.3 percent and brand 18.0 percent, keycap manufacturer 56.9 percent, profile 71.0 percent, and material 55.4 percent. The switch catalog is version 11 and the keycap catalog version 11.

Three inferences were tested and rejected for lack of evidence.

- Switch type from a type word in the name would fill nothing. The existing rule already agrees on 457 entries and has caught everything. Its one disagreement, `Hi-Tek 725 Black Clicky (Two Eye)` recorded as tactile, is a data error rather than a rule.
- Silent and Hall effect still establish nothing on their own. Silent-named switches split 101 linear to 41 tactile, and Hall effect splits 124 to 12.
- Keycap material has no rule left. No keycap name carries PBT or ABS as a word, EnjoyPBT runs 4 PBT against 1 ABS, and Cherry profile runs 1,015 ABS against 90 PBT.

## Confirmed keycap lines and the XMI source gap — 2026-09-10

Maintainer-confirmed rules, filling 48 values across 52 entries:

- XMI and CRP sets are Cherry profile and PBT.
- CRP-X is its own shape and also PBT. Its hyphen stops CRP's Cherry default from reaching it, because the alias prefix only matches before a space, comma, slash, or open bracket.
- JTK-labelled sets are Cherry profile and ABS, made by JTK.
- HSA is JTK's shape and is always ABS, so an HSA set records JTK without naming it.

### XMI coverage

The catalog holds one XMI entry, `XMI Vintage Support Extension`. That is not a filter problem. Checking the cached snapshot, KeycapLendar carries exactly one XMI record, and no other active source mentions XMI at all. The single apparent hit in SwitchOddities is the substring inside an image filename.

Searching for a source that does carry XMI found almost nothing. Across NovelKeys, CannonKeys, Divinikey, KBDfans, Omnitype, ilumkb, Daily Clack, Swagkeys, KeebsForAll, Norbauer, and Keygem, only one XMI listing exists, `XMI Matcha Sakura` at Keygem. XMI runs its group buys through Chinese platforms, and zFrontier exposes no reachable product feed. keycapsets.com is a live keycap database whose data is fetched client side, and its endpoint was not discoverable from its static assets. Closing the XMI gap needs either a vendor with a real feed that carries the line, or a manual list.

### Other keycap sources worth adding

These are ordinary Shopify feeds that the existing generic adapter already handles. Counts are keycap-shaped listings whose names are absent from the accepted catalog, measured on 2026-09-10. Together they hold 365 distinct new names, none of them XMI.

| Source | Listings | Not in catalog |
|---|---:|---:|
| [NovelKeys](https://novelkeys.com/collections/keycaps/products.json?limit=250&page=1) | 155 | 119 |
| [CannonKeys](https://cannonkeys.com/collections/keycaps/products.json?limit=250&page=1) | 119 | 89 |
| [Daily Clack](https://dailyclack.com/collections/keycaps/products.json?limit=250&page=1) | 132 | 48 |
| [Omnitype](https://omnitype.com/collections/keycaps/products.json?limit=250&page=1) | 49 | 43 |
| [Keygem](https://keygem.com/collections/keycaps/products.json?limit=250&page=1) | 62 | 39 |
| [Dangkeebs](https://dangkeebs.com/collections/keycaps/products.json?limit=250&page=1) | 24 | 19 |
| [Swagkeys](https://swagkeys.com/collections/keycaps/products.json?limit=250&page=1) | 25 | 15 |

Adding any of them needs the `vendor` filter in `Convert-CatalogProduct` widened first. Its allow list is written for the current four retailers, so a new host would record a vendor field such as `Stocked` or `NovelKeys, LLC` as a selling brand.

## Companies that make what they sell — 2026-09-10

Maintainer-confirmed. These keycap companies are both the selling brand and the maker, so a set of theirs records one company rather than two: CreateKeebs, Domikey, EnjoyPBT, Gateron, GoMaster, Keyboard Science, Keyreative, Milkway Keys, PBTfans, SoulCat, Swagkeys, TutKeys, Vividkey, Wuque Studio, and XMI. Together with the earlier GMK, KeyKobo, and JTK entries, 19 companies now declare a keycap maker. This filled 385 manufacturers and removed 368 brands that had become redundant.

- CRP is the exception. Hammerworks builds the line and CRP sells it, so `keycapDefaults.manufacturer` now accepts a company name as well as `true`. A CRP set records manufacturer Hammerworks and brand CRP.
- CRP-X is Hammerworks' own shape and declares that maker itself. Two entries in the line do not lead with CRP, `CRP-X Parallel Worlds` and `Hammerworks CRP R7 Beige`, so reviewed overrides record the line's brand on them.
- XMI also trades as 21KB and Xiami; both resolve to XMI.
- Milkway Keys is now the canonical spelling for the MW line, replacing Milkyway. The change carried through 16 manufacturer and designer values, including the combined credit `Skok & Milkway Keys`.
- Vividkey uses `VVD` in product names, renaming its two sets.
- TutKeys is canonical for the `TUT` prefix, and Vividkey, TutKeys, and Hammerworks are new alias entities.
- NovelKeys and RAMA Works remain sellers only, and the reviewed omission on `PBTfans Thermal` still holds, so that set records a brand and no maker.

Keycap coverage is now manufacturer 73.5 percent, profile 72.5 percent, and material 57.1 percent, against 34.4, 36.7, and 18.1 percent before this session's inference work. The keycap catalog is version 13.

## Both company roles, and two-space JSON — 2026-09-10

A company that both sells and makes a product now appears in `brand` and `manufacturer`, so a Domikey set records Domikey twice. The earlier rule that dropped a brand matching its manufacturer is gone from all three places it lived.

Dropping the duplicate looks tidy but is not lossless here. 1,804 switch and 351 keycap entries already carry a manufacturer and no brand, meaning the maker is known and the seller is not. `8008 Ink` is made by Gateron and `Acer White` by KTT, with no established seller for either. If a missing brand meant "same as the manufacturer", those 2,155 entries would be read as self-branded. Keeping both fields costs about 126 KB and keeps the two states apart.

Milkyway Keys is the canonical spelling, with `Milkway Keys` retained as an alias so the misspelling still resolves on import.

`ConvertTo-CatalogJson` now writes two-space JSON through its own writer, because Windows PowerShell's `ConvertTo-Json` has no indentation option. Non-ASCII characters are written literally rather than as `\uXXXX` escapes, so a CJK colourway reads normally in a diff.

| File | Before | After |
|---|---:|---:|
| `sources.json` | 6,355 KB | 3,732 KB |
| `switches.json` | 1,354 KB | 780 KB |
| `keycaps.json` | 784 KB | 440 KB |
| `overrides.json` | 399 KB | 234 KB |
| `aliases.json` | 34 KB | 14 KB |
| Total | 8,926 KB | 5,200 KB |

Formatting is not catalog content, so this changed no catalog version. Reformat `overrides.json` and `aliases.json` before replaying, because the promotion receipt hashes them and a later rewrite invalidates it.
