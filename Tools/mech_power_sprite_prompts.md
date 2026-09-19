## Bastion revision: heavy industrial chassis

Mode: built-in image_gen. Replaces the original Bastion prompt above.

Create a NEW DSM Bastion heavy industrial siege mech sprite sheet for SS14, following the attached repo Gilgamesh sprite reference EXACTLY for native pixel scale and squat industrial weight. Second Paladin reference only guides regal machine identity. SIX frames on transparent background, 3 columns by 2 rows: south, north, east; west, open cockpit facing south, damaged standing facing south. These are enlarged nearest-neighbor 48x48 pixel cells. Match truly sparse 16-color native pixel artwork, big clean pixel clusters, no tiny details, soft gradients, glow, halos or rendered texture.
Radically new silhouette: squat armored siege ENGINE with an extremely wide chamfered rectangular shoulder beam, recessed tiny crowned sensor head between shoulders, angular wedge torso cockpit and short broad pillar legs. Distinct hydraulic forearms hanging below shoulder beam with exposed elbow gaps, 2 barrel-like vents on back, layered slab armor, large broad feet. NO muscular pectoral shapes, no human waist, no knight suit anatomy, no round pauldrons. Gilgamesh is the primary proportion and style reference.
Royal identity through an INTEGRATED SMALL 3-point crown-shaped gold sensor crest, not an enormous wearable crown. Very dark royal purple slabs over charcoal black chassis, thin old gold accents along upper shoulders and crown only, tiny cyan sensor slit. NO WHITE, ivory or silver, NO bright gold knees or gold limbs. A squared crown/visor head, about 5x5 logical pixels, broad total machine about34x34 pixels per48x48 cell. Make it look massive and purpose built, and distinctly different from a humanoid armored knight.
All four directions must show the same geometry and equipment, facing right/left correctly. Open version has chest hatch raised showing dark cockpit seat, crown sensor retained; damaged version lost one forearm, fractured plates, cables exposed. Maintain the same scale, foot baseline, ample transparent margin. No text, grid, decorative scene, ground shadow. EXACT flat low-res pixel art matching the inputs, actual transparent alpha.

## Power indicator

Retired design: the floating indicator was removed. See
`mech_visor_sprite_prompts.md` for the current integrated sensor masks.

Mode: built-in image_gen. Output: `Resources/Textures/_Crescent/Objects/Specific/Mechs/power_indicator.rsi/`.
Each module is reduced to 9x4 pixels, with binary alpha, then placed at (19, 1) in a 48x48 transparent overlay frame. Three one-direction states: `powered`, `low`, `off`.

Game asset sprite sheet for SS14 industrial mech power indicator. TRANSPARENT alpha background. Three equal columns, ONE row, identical tiny horizontal rectangular status modules with a dark charcoal metal housing and exactly three square LED segments horizontally inside each housing. Left module: all three LEDs bright cyan. Middle module: first LED amber, other two dark gray. Right module: all three LEDs dark gray, completely unlit. Each module is a tiny 9 by 4 logical pixel sprite enlarged enormously with nearest-neighbor only. Flat hard square pixel clusters, 1 pixel dark border, no glow, no halo, no shadow, no glass, no gradients, no texture, no text, no labels or background. Same housing silhouette and dimensions in all three columns. Generous transparent margins. Minimal pixel art for a tiny world sprite overlay.
