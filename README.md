# Statistical Similarity of Binaries

This repo holds the semantic component from the __Esh__ tool described in the _Statistical Similarity of Binaries_ paper.

For more information about __Esh__, the paper, and a demo of what the code available here does, please visit http://BinSim.com

Notes:
* The project was built with VS2015 under Win8. 
* Other versions of VS\Win may also work.
* Once built, the executable can also be run with `mono` under Linux, etc.

Instructions:
0. Don't open the solution in VS just yet!
1. Get the _Boogie_ project from `https://github.com/boogie-org/boogie`, build it, and copy following files to the `references/` directory:
```
AbsInt.dll
Basetypes.dll
Boogie.exe
CodeContractsExtender.dll
Concurrency.dll
Core.dll
Doomed.dll
ExecutionEngine.dll
Graph.dll
Houdini.dll
Model.dll
ModelViewer.dll
Newtonsoft.Json.dll
ParserHelper.dll
Predication.dll
Provers.SMTLib.dll
VCExpr.dll
VCGeneration.dll
```
2. Get _Z3_ from `https://github.com/Z3Prover/z3` and place the executable (`z3.exe`) under `Bin/`.
3. Now open the solution and build.
4. Go to `Bin/` and try running with `BplMatch.exe toy1.bpl toy2.bpl Query`