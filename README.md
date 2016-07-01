# Statistical Similarity of Binaries

This repo holds the semantic component from the __Esh__ tool described in the _Statistical Similarity of Binaries_ paper.

For more information about __Esh__, the paper, and a __demo__ of what the code available here does, please visit http://BinSim.com

### Notes:
* The project was built with VS2015 under Win8. 
* Other versions of VS\Win may also work.
* Once built, the executable can also be run with `mono` under Linux, etc.

### Instructions:
1. Don't open the solution in VS just yet!
2. Get <a href="https://github.com/boogie-org/boogie">_Boogie_</a>, checkout and build at commit 'c8c15f672dc42fca1db9b0f20549ef49b48889e8'.
3. Copy following files to the `references/` directory:
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
3. Get <a href="https://github.com/Z3Prover/z3">_Z3_</a> and place the executable (`z3.exe`) under `Bin/`.
4. Now open the solution and build.
4. Go to `Bin/` and try running with `BplMatch.exe toy1.bpl toy2.bpl Query`
