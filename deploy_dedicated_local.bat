@echo off
rmdir /S /Q "C:\Users\dangu\AppData\Roaming\SpaceEngineersDedicated\Mods\Automata" 2>NUL
mkdir "C:\Users\dangu\AppData\Roaming\SpaceEngineersDedicated\Mods\Automata" 2>NUL
mkdir "C:\Users\dangu\AppData\Roaming\SpaceEngineersDedicated\Mods\Automata\Data" 2>NUL

xcopy "C:\Users\dangu\source\repos\Automata\Automata\modinfo.sbm" "C:\Users\dangu\AppData\Roaming\SpaceEngineersDedicated\Mods\Automata\" /Y /Q 2>NUL
xcopy "C:\Users\dangu\source\repos\Automata\Automata\Automata.ini" "C:\Users\dangu\AppData\Roaming\SpaceEngineersDedicated\Mods\Automata\" /Y /Q 2>NUL
xcopy "C:\Users\dangu\source\repos\Automata\Automata\Data" "C:\Users\dangu\AppData\Roaming\SpaceEngineersDedicated\Mods\Automata\Data\" /S /Y /Q 2>NUL