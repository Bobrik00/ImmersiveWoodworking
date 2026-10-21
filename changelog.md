# Changelog

Player-facing release notes for ImmersiveWoodworking — posted to ModDB. Newest on top.
Full developer detail lives in `changelog-internal.md` and is never posted here.

How to write an entry — short, direct, honest. No fluff, no selling:
- One line per change. Say WHAT changed, in general terms, from the player's side — not how it works.
- Cut filler adjectives ("cleaner", "tidied-up", "seamless", "improved", "better").
- Keep internal detail out — config keys, exact thresholds/percentages, tool/file/class names,
  every edge case and station it touches. That's what `changelog-internal.md` is for.
- Give the shape of the change, not a full spec. If in doubt, cut.
    Overloaded: "...while the log is less than 25% processed now returns the whole log; past 25%
                it's consumed. Works on the sawhorses (sawing and debarking) and the automated
                sawmill. Configurable (LogRecoverProgressFraction, 0 = ..., 1 = ...)."
    Right:      "Logs can now be recovered from a station below a certain processing point."

## 1.3.14
- No more "Could not parse" warnings for other mods' recipe files that the game itself reads fine (seen with Tankards, Goblets and More). Such files are now handled like any other.
- Half-log slabs and logs on the stations now take their textures from the log itself when a tree mod keeps them in an unusual place, instead of showing aged wood or a blank texture.
- Fixed bark handbook pages missing the list of woods the bark comes from, and the chopping block and sawhorse pages showing raw placeholders instead of text.
- Bark handbook pages now show the logs the bark is stripped from as clickable icons, the same way log pages show their bark.
- The ordinary bark description no longer says it only comes from woods without a special bark.
- The bark-per-log setting now works for every tree. Before, the vanilla trees ignored it and always dropped 3. Bark per log can now also be set for each wood separately in the config.
- Forestry XP from chopping, sawing and debarking (with xSkills) is doubled. A server that already has a config keeps its old XP numbers until it changes them.

## 1.3.13
- Fixed a sawhorse rip that could freeze partway and keep dropping planks or beams without end. If another mod's error was behind it, the log now says so.

## 1.3.12
- Server owners can now switch off the automated sawmill, the automated chopper or the whole bark chain in the config. A switched-off part can no longer be made and disappears from the creative inventory and the handbook; machines already built keep working.
- With the bark chain off, stripping a log on the sawhorses still gives the debarked log but no bark, and the vanilla way to make tannin from logs comes back.
- Birch tar can be boiled down into resin in a clay pot on a firepit, for the things that take nothing but resin, such as the water wheel.

## 1.3.11
- Fresh bark dries faster near heat: a lit firepit, forge, clay oven or anything else that warms you speeds it up, in a pile on the ground or in a chest.
- A pile of fresh bark on the ground now dries as fast as bark in your inventory, and shows how far it has dried and how fast it is drying.

## 1.3.10
- Added compatibility with Food Shelves: plain bark, dried tanning and birch bark, medicinal bark, crushed tanning bark and birch tar now go into its jars, including the Large Jar on a Ceiling Rack. Fresh bark and bast stay out.
- Bark now shows as a layer inside a barrel with no liquid and inside an open crate; a crate holding bark no longer stays shut.

## 1.3.9
- Bark decoction now brews up to three portions in one cook. Medicinal bark goes into three pot slots instead of one, as the recipe on its handbook page shows; the bark cost per poultice is unchanged.

## 1.3.8
- Added compatibility with Seraph Leveling ReClassed: boards you rip on the sawhorses now count toward the Carpenter trait, the same as boards crafted in the grid.
- Class recipes locked behind a trait are no longer removed with the vanilla grid recipes, so Sacred Classes' Carpenter keeps its planks, beams and firewood recipes.

## 1.3.7
- Fixed the perforated pot and both tar pots showing broken or missing textures after you leave a world and join again without restarting the game.
- Fixed a pot stored on the ground in that state freezing the look of everything nearby: it was invisible, and items picked up around it stayed drawn on the floor.

## 1.3.6
- The bark decoction's handbook page now points to the poultice you make from it, not just how to brew it.
- A log's handbook page now shows which bark you get from stripping it, with the odds for each debarking tool.

## 1.3.5
- A new server setting drops everything the chopping block and the sawhorses produce at your feet instead of around the station. It is off by default.
- Logs from a tree mod that turn into a differently-named vanilla plank (a Siberian pine log into pine planks, say) can now be sawn at the sawhorse and the automated sawmill, instead of being refused.

## 1.3.4
- Messed up with the retracting, lmao

## 1.3.3
- You can now choose what a log is sawn into, planks or support beams, instead of it being fixed by whether the log still has its bark. Any log, barked or stripped, can become either at the sawhorses or the automated sawmill. Pick the cut with the saw's tool mode (press F at a sawhorse), or cycle it with Shift and right-click on the sawmill.
- The handbook pages for weak and strong tannin now show the bark that goes into the barrel, where there used to be an empty slot. The bark compost recipe shows its bark too.

## 1.3.2
- Stripping a log now yields more bark than before. The overall amount is a setting in the main config, while how much each kind of wood gives is set per species in a file inside the mod.

## 1.3.1
- Chopping a log through to firewood now wears the axe more, and splitting a log into half-logs costs durability instead of being free.
- Aged wood works the whole way through now: an aged log chops into aged firewood, that firewood splits into sticks at the same station, and aged half-logs can be placed.
- A ruined axe or saw no longer works at steel speed; it drops to the slowest tier of its kind. Axes and saws added by other mods now work at a speed matched to their own tier instead of the fastest one, so some of them are slower than they were.
- Support beams can no longer be sawn from the worn wood states the game's own beam recipe leaves out.
- A log a station can make nothing from is now refused with a message instead of being worked down to nothing, which under auto-feed used to take the rest of the stack with it.
- Settings a server has turned off or set to zero now reach players, instead of the client falling back to its own defaults.
- The handbook now describes what your server's settings actually allow instead of quoting fixed numbers, lists the tree species your game really has, and covers the bark and tar line. Every tar pot and perforated pot opens one page for its item, and the debarked log page says where to strip a log.
- Sawdust now comes off the side the cut is actually on and starts where the saw bites into the log. It settles and fades sooner, and hand sawing throws the same amount of it whatever your framerate.
- You now hear yourself loading and unloading logs at the machines, other players see your sawdust and hear you debarking, and snapping driftwood has a sound at last.
- The chopper's running sound no longer falls silent and restarts when its power flickers.
- The sawmill's feed ratchet no longer turns backwards depending on which way the machine faces, and leaving a sawmill and coming back no longer pushes its cut forward for free.
- The automated sawmill and chopper each have a speed setting now. They still run only as fast as the mechanical network drives them; the setting changes how much work a log costs, so you can make either one get through logs faster or slower without changing the drive. At the default it behaves exactly as before.
- A sawmill or chopper driven by a very fast gear train no longer freezes; it keeps working at the speed the network turns it.
- Using or breaking the outer blocks of a sawmill, an automated chopper or a sawhorse pair is now checked against the land claim on the machine itself.
- Breaking driftwood now needs the same permission as breaking any other block, and reinforcing driftwood now protects it.
- Hand sawing and debarking now run for their full time, so they take a little longer than they did.
- Translation fixes: the woodworking handbook page has a translated title, the Russian text of the xSkills Careful Sawyer ability matches the rest of the mod, and Russian item names no longer capitalise the wood species mid-sentence.

## 1.3.0
- Debarking a log on the sawhorse now yields bark, and the kind depends on the tree.
- Tanning bark steeped in a barrel now gives tannin, without consuming a whole log. The old whole-log tannin recipe is off by default and can be turned back on in the config.
- Birch bark can be packed into a perforated clay pot, sealed with clay and fired in a pit kiln to make birch tar. Tar can be used in place of pine resin, glue included.
- Bast, a fibrous inner bark, can be twisted into rope or made into a poultice.
- Medicinal bark can be simmered into a decoction and made into healing poultices.
- Ordinary bark can be composted in a barrel, used in place of sticks in a pit kiln
- Half-logs can now be placed as building blocks. 
- Half-logs split from a wood that its own mod made non-flammable, such as Wildcraft Trees' petrified wood, no longer burn as fuel.

## 1.2.2
- Chopping firewood into sticks now gives a little forestry experience with xSkills. It used to give none. The amount can be changed, or set back to none, in the config.
- Blowing up a sawmill or automated chopper no longer leaves invisible blocks where the machine stood. Leftovers from earlier explosions can now be mined away.
- Fixed a case where holding right click with an axe on the chopping block, without meaning to chop, could pull a log from your pack and wear the axe down.
- Sawmills and automated choppers are lighter on performance, which shows most when many of them run at once on a server.

## 1.2.1
- Added the splitting maul, a forged tool that chops a whole log straight into firewood, skipping the half-log stage.
- The config was split from one file into a folder of smaller files. Your existing settings carry over automatically, so there is nothing to do by hand. If you later downgrade the mod your settings reset to defaults. One older setting, how much durability an axe spends per log while chopping, was renamed to read directly; if you had changed it from the default, set it again after updating.
- The automated sawmill and chopper can now be set to give different yields than the hand stations. By default they match, so nothing changes unless you want it to.
- Chopping a log now takes a slightly varying number of strikes instead of always the same. It can be turned off in the config.
- Fixed the log warnings some players saw when running mods that add metals. Any manual patch you used to silence them can be removed.
- The two-handed pit saw now works in the cabinet and door recipes regardless of other mods.
- A half-log now changes its look a little earlier as you chop it.

## 1.2.0
- Added optional integration with the xSkills mod. When xSkills is installed, sawing, debarking and chopping logs by hand now train your Forestry skill.
- With xSkills, four new Forestry abilities become available: Sawyer (more planks and beams from sawing), Splitter (more firewood from splitting), Careful Sawyer (a chance to spend no tool durability while sawing or debarking), and Swift Hands (faster sawing and chopping).
- Without xSkills nothing changes: the mod works exactly as before.

## 1.1.2
- Fixed the third person chopping animation so the character no longer leans forward unnaturally while chopping on the block.
- Sawing a log on the sawhorse is now directional: click the side of the log you want, and the boards fall and the log is cut away from that side.
- Debarking is now directional too: click the end you want to start from and the bark peels from that end. The stripped log always drops on the far side, away from you.


## 1.1.1
- Added an option to chop logs straight into firewood, skipping the half-log step.
- Added an option to disable chopping firewood into sticks.

## 1.1.0
- Added an automatic log splitter.
- The pit saw and bark spud now integrate with the Toolsmith mod: forge a dedicated head, then assemble and sharpen the tool on Toolsmith's workbenhc a sharp edge rips and strips faster than a blunt one.
- Added a forged pit saw blade.
- Reorganized the config file and renamed several options for clarity, and removed its in-file comments for wider mod-manager compatibility. Custom mod settings will reset to their defaults after this update. If this doesn't happen, you need to delete the config manually so the game recreates it on startup - otherwise the default values will be used.
- Removed the ability to chop driftwood on the chopping block, but added the ability to snap driftwood in your hands (because it's logical and fun).

## 1.0.9
- Fixed a conflict with the VSRoofing mod where building a sawhorse and raising a roofing frame both fired on the same right-click.
- Log sections can now be sawn into planks on the sawhorses or the automated sawmill. For now the quarter-log looks like a regular log on the sawhorse.

## 1.0.8
- Chopping a log by hand now first splits it into two half-logs, which you then chop into firewood. A whole log still yields the same amount of firewood in the end.
- A log on the chopping block is now visibly hewn apart in stages as you chop it.
- Logs and half-logs on the chopping block now turn to face you when you start chopping.

## 1.0.7
- Chopping a log on the chopping block now takes several axe strikes instead of one, with a progress readout as you work.
- An axe takes its durability as you chop, spread across the strikes.

## 1.0.6
- Ripping and debarking logs by hand now go faster or slower with your tool's tier.

## 1.0.5
- Rebalanced firewood and stick yields so every wood output is consistent across the stations.

## 1.0.4
- Updated the models for the automated sawmill's crafted parts.
- Character now carries the sawmill parts with both hands.

## 1.0.3
- The automated sawmill's selection outline now follows the machine's shape instead of a box around the whole thing.

## 1.0.2
- You can now stick an axe into the top of a chopping block to park it there.

## 1.0.1
- Logs can now be recovered from a station if processing hasn't gone too far.
- Translation fixes.
- Reworked the chopping block model - it's a bit less round now.

## 1.0.0
- Added debarking mechanic
- Some minor code changes

## 0.9.13
- Changed the visual size of logs on the chopping block and the sawbuck. 
- New the chopping block model to match the new size (subjective, but I liked the previous version more :P)

## 0.9.12
- Handbook pages for planks, support beams, firewood, sticks and logs now tell you where they're made - with links to the sawhorses, automated sawmill and chopping block. 

## 0.9.11
- Fixed the chopping animation freezing and losing sync while continuously chopping (auto-restock)

## 0.9.10
- Compatibility with WearAndTear
- Minor code cleanup (no gameplay changes)

## 0.9.9
- Fixed the missing chopping block recipe in the handbook
- Another fix for broken textures on sawhorses
- The sawhorse guide has been updated to be more specific. Now, depending on how the sawhorse is set to be obtained, it'll show either the crafting description or the grid recipe.

## 0.9.6

- Added an optional way to get the sawhorse by crafting it (stick + rope + stick) and placing
  it like a normal block, instead of building it from sticks in the world.

## 0.9.5

- Added compatibility with other wood mods (e.g. Wildcraft: Trees): chopping blocks now
  work with any wood type, not just the base game's.
- Chopping blocks placed in existing worlds are kept after updating.
- Downgraded to 1.22.2

## 0.9.4
- Added auto-placement that feeds matching blocks from your inventory

## 0.9.3
- Fixed the buggy texture on the sawhorses

## 0.9.2
- Changed the pitsaw recipe, since the old one didn't work with high-tier metals
- Added support for the regular saw and saws from other mods (work at 0.5 the speed of the pitsaw, configurable in the config)