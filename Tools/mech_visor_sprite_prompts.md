# Integrated mech power lights

Mode: built-in image_gen. The light swatches below are cropped and packed by
`Tools/pack_mech_power_masks.py` into the seven existing chassis RSI directories
at `Resources/Textures/_Crescent/Objects/Specific/Mechs/`.

The previous floating battery indicator is retired. Its RSI has been removed.
Each chassis now uses pixel-aligned visor/sensor and rear service-light masks.
The underlying chassis images remain unchanged. Every mask is entirely inside
its matching chassis silhouette. Closed masks have four directions; cockpit-open
and broken masks match their single-direction hull state. Broken hulls are unlit.
Normal charge uses the faction sensor color, low charge a dimmer version, and
no power a dark shaded lens. The client applies #606060 to the off swatch.

## Source prompt

Transparent pixel art game asset: six tiny LED light-strip swatches in a precise THREE column TWO row sheet. Each swatch is only a perfectly SOLID FILLED horizontal rectangle with a 3:1 aspect ratio, to be cropped and packed into an existing SS14 mech visor. Upper row left bright pale cyan, center dim desaturated teal, right dark charcoal gray. Lower row left bright amber yellow, center dim desaturated ochre, right dark charcoal gray. All six same rectangular dimensions and aligned with generous transparent space between. OPAQUE solid flat color inside each rectangle; TRANSPARENT everywhere outside. Hard pixel edges only. No housing, border, icon, battery, text, gradient, glow, blur, lighting, shadow or other objects. Exact tiny minimal pixel art light strips enlarged by nearest-neighbor.
