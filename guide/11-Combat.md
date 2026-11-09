To be clear, Combat is not really handled by Automata.

The Drone Controller is only responsible for undocking, out-of-combat navigation, and docking.

The AI Defensive block takes care of enemy detection. If the drone's Behavior Profile is set to **Combat drone**, the drone controller listens to the AI Defensive block's enemy detection, and then hands control over to the AI Offensive and AI Flight block.

Its important to setup the AI blocks before hand, or your drone might not perform as you expect:
- AI Flight: speed limits, align to P-Gravity, minimum altitude, and pitch/roll max.
- AI Combat: Configure static weapons, and attack strategy

Using a Hostile Combat Designator, or the Target Designator block, 

- **Engage**: point at an enemy or neutral grid or character, or at a spot on the ground. The drone flies there, and once within its **handover distance** switches on its AI Flight and AI Offensive blocks to fight. It returns home when the target is destroyed or disabled, or when it has found no targets for a while. Pull the trigger again to send another drone.
- **Escort**: point at a mobile grid, or a friendly player. The drone follows above it at the **follow distance**, fights any enemy that comes close, then resumes following. Point at the ground next to yourself to have it escort you.

Combat drones come home on their own when power or hydrogen runs low, if their AI Defensive block is configured to flee, or when an AI block is lost. You can tune all of this in the drone's **Combat** section.
