# Mech RSI redesign

Mode: built-in image_gen. Inputs are nearest-neighbor enlarged game art, used as style references. Generated sheets are reduced and packed mechanically into 48x48 RSI frames.

The approved game assets live in `Resources/Textures/_Crescent/Objects/Specific/Mechs/`.
Each source sheet has south/north/east above west/open/broken. Package all seven
approved source PNGs using:

```
python Tools/pack_faction_mech_sprites.py <source-directory> --preview <preview.png>
```

Pass `--output <review-directory>` to review a batch before replacing game assets.
Packing uses nearest-neighbor scaling, binary alpha and a shared 24-color palette
per chassis. Sensor colors are retained with maximum-coverage quantization.
The older `generate_faction_mech_sprites.py` creates obsolete procedural drafts;
do not use those drafts to regenerate the approved game sprites.

For Oni, Ravager and Svarog, the final prompt also included:
"Use a strictly limited 16-color indexed pixel palette, flat color clusters;
remember reference dark factory machinery, not shiny overrendered concept armor."

## Bastion

Use case: style-transfer.
Asset: playable SS14 HULLROT mech sprite sheet, six states, transparent alpha background.
The attached image is a STRICT PIXEL STYLE and sheet geometry reference, a nearest-neighbor 8x enlargement of actual 48x48 game cells. Design a new related chassis belonging to this very same sprite set. Match its extremely low resolution pixel clusters, compact mechanical proportions, black seams, muted stepped 3-4-tone material ramps, discrete pixel highlights and restrained detail density. Keep the slight top-down SS14 perspective and planted feet. Not concept art; not smooth illustration; not a 3D render; not humanoid superhero armor.
MANDATORY PIXEL GRID: work as though drawing at exactly 144x96 total pixels, each state exactly 48x48, then enlarge with nearest-neighbor only. Every pixel is a large identical square block. No subpixel details, no antialias, gradients, noise, glow, soft shadow, dither, smooth curves, or fine realistic detail. An individual head must be only about 5 logical pixels wide. Avoid thick cartoon outlines and flat rectangular toy robots. Use varied angled plate edges, readable piston joints, tiny negative spaces separating limbs and torso. No attached weapon.
LAYOUT: exactly THREE equal columns and TWO equal rows. Upper row: facing south/front, north/back, east/right profile. Lower row: west/left profile, south/front with cockpit hatch OPEN showing a dark seat cavity, south/front DAMAGED with one arm broken and torn panels/exposed wires. All six depict the same chassis. Closed standing frames and open frame share identical foot baseline and scale. Damaged is still upright like the reference broken state, not a separate debris pile. Keep generous transparent margins. No text, labels, border, ground, background or grid lines. Background must be actual transparent alpha.
Reference art is for stylistic guidance. Create a distinct new design, not a simple palette swap.
CHASSIS: DSM BASTION, heavy T3 aristocratic siege mech. Use image 1 Gilgamesh for weight/proportions and image 2 Paladin for industrial knighthood. Broad faceted shoulders, dense angular torso with central dark hatch, short powerful mechanical legs. Integrated small royal helmet: unmistakable 3-pronged GOLD crown built into the head, short cyan slit visor beneath, dark face plate. Crown stays visible on all directions and the open state. Armor predominantly near-black charcoal and very dark royal purple; limited muted old-gold trim on crown, shoulder edges and knee plates. Cyan ONLY in tiny visor pixels. ABSOLUTELY NO WHITE, cream, ivory, silver armor or pale highlights. Sparse muted gold accents, not solid gold limbs. Strong silhouette within about34x39 logical pixels of each48x48 cell.

## suzume

References: gilgamesh, lancer

Use case: style-transfer.
Asset: playable SS14 HULLROT mech sprite sheet, six states, transparent alpha background.
The attached image is a STRICT PIXEL STYLE and sheet geometry reference, a nearest-neighbor 8x enlargement of actual 48x48 game cells. Design a new related chassis belonging to this very same sprite set. Match its extremely low resolution pixel clusters, compact mechanical proportions, black seams, muted stepped 3-4-tone material ramps, discrete pixel highlights and restrained detail density. Keep the slight top-down SS14 perspective and planted feet. Not concept art; not smooth illustration; not a 3D render; not humanoid superhero armor.
MANDATORY PIXEL GRID: work as though drawing at exactly 144x96 total pixels, each state exactly 48x48, then enlarge with nearest-neighbor only. Every pixel is a large identical square block. No subpixel details, no antialias, gradients, noise, glow, soft shadow, dither, smooth curves, or fine realistic detail. An individual head must be only about 5 logical pixels wide. Avoid thick cartoon outlines and flat rectangular toy robots. Use varied angled plate edges, readable piston joints, tiny negative spaces separating limbs and torso. No attached weapon.
LAYOUT: exactly THREE equal columns and TWO equal rows. Upper row: facing south/front, north/back, east/right profile. Lower row: west/left profile, south/front with cockpit hatch OPEN showing a dark seat cavity, south/front DAMAGED with one arm broken and torn panels/exposed wires. All six depict the same chassis. Closed standing frames and open frame share identical foot baseline and scale. Damaged is still upright like the reference broken state, not a separate debris pile. Keep generous transparent margins. No text, labels, border, ground, background or grid lines. Background must be actual transparent alpha.
Reference art is for stylistic guidance. Create a distinct new design, not a simple palette swap.
CRITICAL: absolutely NO glowing halo or semi-transparent colored haze. All metal pixels fully opaque; empty pixels fully transparent. Keep the design just as sparse and low resolution as the references.
CHASSIS: SHI SUZUME, light T1 industrial scout. Lightweight cousin of Gilgamesh (image1), compact torso and relatively narrow shoulders like Lancer (image2). Pearl-gray and cool gray ceramic panels over graphite machinery, tiny cyan single visor slit. A small off-center sensor stalk, segmented compact chest plates, exposed elbow hydraulics and slim separate mechanical lower legs. Not a fat armored human. Head integrated low between shoulders. NO huge cyan chest window. Slight angular taper through chest and hips. About28 pixels wide and33 pixels high inside48x48; comparable height to Lancer reference. Restrained dull ceramic highlights, black joints, no multicolor ornaments.

## jaipei

References: bogatyr, gilgamesh

Use case: style-transfer.
Asset: playable SS14 HULLROT mech sprite sheet, six states, transparent alpha background.
The attached image is a STRICT PIXEL STYLE and sheet geometry reference, a nearest-neighbor 8x enlargement of actual 48x48 game cells. Design a new related chassis belonging to this very same sprite set. Match its extremely low resolution pixel clusters, compact mechanical proportions, black seams, muted stepped 3-4-tone material ramps, discrete pixel highlights and restrained detail density. Keep the slight top-down SS14 perspective and planted feet. Not concept art; not smooth illustration; not a 3D render; not humanoid superhero armor.
MANDATORY PIXEL GRID: work as though drawing at exactly 144x96 total pixels, each state exactly 48x48, then enlarge with nearest-neighbor only. Every pixel is a large identical square block. No subpixel details, no antialias, gradients, noise, glow, soft shadow, dither, smooth curves, or fine realistic detail. An individual head must be only about 5 logical pixels wide. Avoid thick cartoon outlines and flat rectangular toy robots. Use varied angled plate edges, readable piston joints, tiny negative spaces separating limbs and torso. No attached weapon.
LAYOUT: exactly THREE equal columns and TWO equal rows. Upper row: facing south/front, north/back, east/right profile. Lower row: west/left profile, south/front with cockpit hatch OPEN showing a dark seat cavity, south/front DAMAGED with one arm broken and torn panels/exposed wires. All six depict the same chassis. Closed standing frames and open frame share identical foot baseline and scale. Damaged is still upright like the reference broken state, not a separate debris pile. Keep generous transparent margins. No text, labels, border, ground, background or grid lines. Background must be actual transparent alpha.
Reference art is for stylistic guidance. Create a distinct new design, not a simple palette swap.
CRITICAL: absolutely NO glowing halo or semi-transparent colored haze. All metal pixels fully opaque; empty pixels fully transparent. Keep the design just as sparse and low resolution as the references.
CHASSIS: SHI JAIPEI, medium T2 industrial combat mech, the Shinohara family ancestor of the Bogatyr in image1. Use Bogatyr upright mechanical posture and angular sternum plates, and Gilgamesh white/cool gray + graphite material treatment (image2). Distinct squared but chamfered compact shoulder shells, reinforced segmented forearms, dark elbow gap, cyan tiny visor recessed in small head, inset central graphite cockpit armored hatch. Pearl gray panels on shoulders chest shins, dark exposed hips and knee hinges. Broad upper torso, slim waist, sturdy separated legs. NO huge glass window or billboard flat chest. About30x37 logical pixels per48x48 cell; native repo mecha detail level.

## yamori

References: marauder, lancer

Use case: style-transfer.
Asset: playable SS14 HULLROT mech sprite sheet, six states, transparent alpha background.
The attached image is a STRICT PIXEL STYLE and sheet geometry reference, a nearest-neighbor 8x enlargement of actual 48x48 game cells. Design a new related chassis belonging to this very same sprite set. Match its extremely low resolution pixel clusters, compact mechanical proportions, black seams, muted stepped 3-4-tone material ramps, discrete pixel highlights and restrained detail density. Keep the slight top-down SS14 perspective and planted feet. Not concept art; not smooth illustration; not a 3D render; not humanoid superhero armor.
MANDATORY PIXEL GRID: work as though drawing at exactly 144x96 total pixels, each state exactly 48x48, then enlarge with nearest-neighbor only. Every pixel is a large identical square block. No subpixel details, no antialias, gradients, noise, glow, soft shadow, dither, smooth curves, or fine realistic detail. An individual head must be only about 5 logical pixels wide. Avoid thick cartoon outlines and flat rectangular toy robots. Use varied angled plate edges, readable piston joints, tiny negative spaces separating limbs and torso. No attached weapon.
LAYOUT: exactly THREE equal columns and TWO equal rows. Upper row: facing south/front, north/back, east/right profile. Lower row: west/left profile, south/front with cockpit hatch OPEN showing a dark seat cavity, south/front DAMAGED with one arm broken and torn panels/exposed wires. All six depict the same chassis. Closed standing frames and open frame share identical foot baseline and scale. Damaged is still upright like the reference broken state, not a separate debris pile. Keep generous transparent margins. No text, labels, border, ground, background or grid lines. Background must be actual transparent alpha.
Reference art is for stylistic guidance. Create a distinct new design, not a simple palette swap.
CRITICAL: absolutely NO glowing halo or semi-transparent colored haze. All metal pixels fully opaque; empty pixels fully transparent. Keep the design just as sparse and low resolution as the references.
CHASSIS: TFCF YAMORI, light T1 raider mech, clearly related to the Marauder(image1) and Lancer(image2) chassis, original distinct silhouette. Low recessed sensor head inside a U-shaped collar, asymmetrical short communications aerial, modest faceted shoulder caps, diagonal overlapping red chest segments and flexible graphite abdomen, thin exposed knee pistons, mechanical claw-shaped hands. TFCF deep muted CRIMSON red on main armor with charcoal/black and dark metal internals. Tiny AMBER optics, no green. Slimmer than Marauder, about27x33 logical pixels per48x48 cell. Keep silhouette compact and leg gap visible.

## oni

References: marauder, bogatyr

Use case: style-transfer.
Asset: playable SS14 HULLROT mech sprite sheet, six states, transparent alpha background.
The attached image is a STRICT PIXEL STYLE and sheet geometry reference, a nearest-neighbor 8x enlargement of actual 48x48 game cells. Design a new related chassis belonging to this very same sprite set. Match its extremely low resolution pixel clusters, compact mechanical proportions, black seams, muted stepped 3-4-tone material ramps, discrete pixel highlights and restrained detail density. Keep the slight top-down SS14 perspective and planted feet. Not concept art; not smooth illustration; not a 3D render; not humanoid superhero armor.
MANDATORY PIXEL GRID: work as though drawing at exactly 144x96 total pixels, each state exactly 48x48, then enlarge with nearest-neighbor only. Every pixel is a large identical square block. No subpixel details, no antialias, gradients, noise, glow, soft shadow, dither, smooth curves, or fine realistic detail. An individual head must be only about 5 logical pixels wide. Avoid thick cartoon outlines and flat rectangular toy robots. Use varied angled plate edges, readable piston joints, tiny negative spaces separating limbs and torso. No attached weapon.
LAYOUT: exactly THREE equal columns and TWO equal rows. Upper row: facing south/front, north/back, east/right profile. Lower row: west/left profile, south/front with cockpit hatch OPEN showing a dark seat cavity, south/front DAMAGED with one arm broken and torn panels/exposed wires. All six depict the same chassis. Closed standing frames and open frame share identical foot baseline and scale. Damaged is still upright like the reference broken state, not a separate debris pile. Keep generous transparent margins. No text, labels, border, ground, background or grid lines. Background must be actual transparent alpha.
Reference art is for stylistic guidance. Create a distinct new design, not a simple palette swap.
CRITICAL: absolutely NO glowing halo or semi-transparent colored haze. All metal pixels fully opaque; empty pixels fully transparent. Keep the design just as sparse and low resolution as the references.
CHASSIS: TFCF ONI, medium T2 assault mech. Chassis belongs to Marauder (image1) and Bogatyr (image2) sprite family. Tiny recessed head with TWO SHORT dark horn-like sensor prongs; red forehead plate, one tiny amber horizontal visor slit. Deep muted CRIMSON red shoulder armor and faceted chest plating over charcoal black machinery. Tapered waist, strong knee housings, exposed elbow pistons, substantial forearm armor; do not add hand weapons. Broad angular shoulders, low integrated head, more armored than Marauder. About31x37 logical pixels per48x48 cell, NOT huge compared with reference.

## ravager

References: gilgamesh, marauder

Use case: style-transfer.
Asset: playable SS14 HULLROT mech sprite sheet, six states, transparent alpha background.
The attached image is a STRICT PIXEL STYLE and sheet geometry reference, a nearest-neighbor 8x enlargement of actual 48x48 game cells. Design a new related chassis belonging to this very same sprite set. Match its extremely low resolution pixel clusters, compact mechanical proportions, black seams, muted stepped 3-4-tone material ramps, discrete pixel highlights and restrained detail density. Keep the slight top-down SS14 perspective and planted feet. Not concept art; not smooth illustration; not a 3D render; not humanoid superhero armor.
MANDATORY PIXEL GRID: work as though drawing at exactly 144x96 total pixels, each state exactly 48x48, then enlarge with nearest-neighbor only. Every pixel is a large identical square block. No subpixel details, no antialias, gradients, noise, glow, soft shadow, dither, smooth curves, or fine realistic detail. An individual head must be only about 5 logical pixels wide. Avoid thick cartoon outlines and flat rectangular toy robots. Use varied angled plate edges, readable piston joints, tiny negative spaces separating limbs and torso. No attached weapon.
LAYOUT: exactly THREE equal columns and TWO equal rows. Upper row: facing south/front, north/back, east/right profile. Lower row: west/left profile, south/front with cockpit hatch OPEN showing a dark seat cavity, south/front DAMAGED with one arm broken and torn panels/exposed wires. All six depict the same chassis. Closed standing frames and open frame share identical foot baseline and scale. Damaged is still upright like the reference broken state, not a separate debris pile. Keep generous transparent margins. No text, labels, border, ground, background or grid lines. Background must be actual transparent alpha.
Reference art is for stylistic guidance. Create a distinct new design, not a simple palette swap.
CRITICAL: absolutely NO glowing halo or semi-transparent colored haze. All metal pixels fully opaque; empty pixels fully transparent. Keep the design just as sparse and low resolution as the references.
CHASSIS: TFCF RAVAGER, heavy T3 siege chassis, a brutal armored industrial cousin of Gilgamesh(image1), TFCF colors like Marauder(image2). Broad compact torso with recessed central armored cockpit, low single amber optic, angular heavy shoulder plates, large short forearms with exposed black elbows, boxy shin armor and broad split-toe feet. Two small exhaust housings on back, visible as discrete pixel shapes. Predominantly dark charcoal graphite, deep blood-red/crimson armor panels at shoulders chest and knees. No brown/orange main armor, no bright red huge flat surfaces. About34x35 logical pixels per48x48 cell, dense squat weight rather than tall human knight.

## svarog

References: gilgamesh, bogatyr

Use case: style-transfer.
Asset: playable SS14 HULLROT mech sprite sheet, six states, transparent alpha background.
The attached image is a STRICT PIXEL STYLE and sheet geometry reference, a nearest-neighbor 8x enlargement of actual 48x48 game cells. Design a new related chassis belonging to this very same sprite set. Match its extremely low resolution pixel clusters, compact mechanical proportions, black seams, muted stepped 3-4-tone material ramps, discrete pixel highlights and restrained detail density. Keep the slight top-down SS14 perspective and planted feet. Not concept art; not smooth illustration; not a 3D render; not humanoid superhero armor.
MANDATORY PIXEL GRID: work as though drawing at exactly 144x96 total pixels, each state exactly 48x48, then enlarge with nearest-neighbor only. Every pixel is a large identical square block. No subpixel details, no antialias, gradients, noise, glow, soft shadow, dither, smooth curves, or fine realistic detail. An individual head must be only about 5 logical pixels wide. Avoid thick cartoon outlines and flat rectangular toy robots. Use varied angled plate edges, readable piston joints, tiny negative spaces separating limbs and torso. No attached weapon.
LAYOUT: exactly THREE equal columns and TWO equal rows. Upper row: facing south/front, north/back, east/right profile. Lower row: west/left profile, south/front with cockpit hatch OPEN showing a dark seat cavity, south/front DAMAGED with one arm broken and torn panels/exposed wires. All six depict the same chassis. Closed standing frames and open frame share identical foot baseline and scale. Damaged is still upright like the reference broken state, not a separate debris pile. Keep generous transparent margins. No text, labels, border, ground, background or grid lines. Background must be actual transparent alpha.
Reference art is for stylistic guidance. Create a distinct new design, not a simple palette swap.
CRITICAL: absolutely NO glowing halo or semi-transparent colored haze. All metal pixels fully opaque; empty pixels fully transparent. Keep the design just as sparse and low resolution as the references.
CHASSIS: NCWL HNF SVAROG, heavy T3 industrial siege mech. Proportions/weight and compact low head like Gilgamesh(image1), EXACT muted charcoal and rusty reddish-brown orange military palette of HNF Bogatyr(image2). Predominantly dark graphite gray, limited dark rusty orange shoulder corners, center chest plate and knees. No olive, khaki, bright yellow or bright orange. Broad layered box shoulders with CHAMFERED corners; small amber rectangular single optic integrated deep between them, a reinforced angular dark torso, vented rear engine backpack, short heavy hydraulic forearms, stout knee joints and broad track-like feet. Strong asymmetry of tiny shoulder utility fixture, no hand weapon. About35x36 logical pixels per48x48 cell. Clearly an NCWL factory-built armored machine, not a human wearing a suit.
