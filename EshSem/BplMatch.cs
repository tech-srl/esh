using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Boogie;
using BType = Microsoft.Boogie.Type;
using System.Diagnostics;
using System.IO;

namespace EshSem
{
    class BplMatch
    {
        private Utils.VarRenamer _renamer;
        private int _eqVarsCounter;
        private int _numOverallAsserts;
        private int _labelCounter;
        private string _v2Prefix;

        private readonly Variable _havocVar;
        private static readonly int MaxAsserts = 10000;

        // important: these should not appear in the input programs
        private static readonly string HavocVarName = "h";
        private static readonly string AssumeVarPrefix = "eq";
        private static readonly string SectionLabelPrefix = "section";

        private static void Usage()
        {
            string execName = Process.GetCurrentProcess().MainModule.ModuleName;
            Console.WriteLine("bplmatch - find the best matching for given query and target.");
            Console.WriteLine("Usage: " + execName + " <query.bpl> <target.bpl> <V2Prefix>");
            Console.WriteLine("-break - start in debug mode");
        }
        static int Main(string[] args)
        {

            if (args.Length < 3 || args.Length > 4)
            {
                Usage();
                return -1;
            }

            if (args.Length == 4 && args[3].ToLower() == "-break")
                Debugger.Launch();

            Utils.SetupBoogie();
            (new BplMatch(args[2])).Run(args[0], args[1]);

            return 0;
        }

        public BplMatch(string v2Prefix)
        {
            _v2Prefix = v2Prefix;
            _renamer = new Utils.VarRenamer(v2Prefix + ".", new string[] { });
            _eqVarsCounter = 0;
            _numOverallAsserts = 0;
            _havocVar = new LocalVariable(Token.NoToken, new TypedIdent(Token.NoToken, HavocVarName, BType.Bool));
        }

        private void Run(string queryName, string targetName)
        {
            Program queryProgram, targetProgram;
            if (!Utils.ParseProgram(queryName, out queryProgram) || !Utils.ParseProgram(targetName, out targetProgram))
            {
                Utils.ErrorAndDie("Boogie parse errors detected.");
                return;
            }

            if (queryProgram.Implementations.Count() != 1 || targetProgram.Implementations.Count() != 1)
            {
                Utils.ErrorAndDie("One Implementation per program, please.");
                return;
            }


            JoinTopLevelDeclarations(queryProgram, targetProgram);

            var queryImplementation = queryProgram.Implementations.Single();
            var targetImplementation = targetProgram.Implementations.Single();

            int numQueryLocals = queryImplementation.LocVars.Count;

            targetImplementation = _renamer.VisitImplementation(targetImplementation);

            var assumeVars = new List<Tuple<Variable, Expr, Expr>>();
            var blocks = CreateAssertsBlocks(queryImplementation, targetImplementation, assumeVars);

            JoinImplementations(queryImplementation, targetImplementation);

            queryImplementation.Blocks.Last().TransferCmd = new GotoCmd(Token.NoToken, blocks);
            queryImplementation.Blocks.AddRange(blocks);

            // print and reload the program
            string joinedFilename = $@"{Path.GetFileName(queryName)}.{Path.GetFileName(targetName)}";
            Utils.PrintProgram(queryProgram, joinedFilename);
            Utils.ParseProgram(joinedFilename, out queryProgram);

            // run Boogie and get the output
            var output = RunBoogie(joinedFilename);

            // find all the failed asserts 
            var failedAssertsLineNumbers = new HashSet<int>();
            var match = Regex.Match(output, @"[(]([0-9]+)[,][0-9]+[)]: Error BP5001: This assertion might not hold");
            while (match.Success)
            {
                failedAssertsLineNumbers.Add(int.Parse(match.Groups[1].Value));
                match = match.NextMatch();
            }

            // map blocks to true asserts
            var trueAsserts = new Dictionary<Block, HashSet<AssertCmd>>();
            queryProgram.Implementations.Single().Blocks.Iter(b =>
            {
                trueAsserts[b] = new HashSet<AssertCmd>();
                b.Cmds.Iter(c =>
                {
                    var ac = c as AssertCmd;
                    if (ac != null && !failedAssertsLineNumbers.Contains(ac.Line))
                        trueAsserts[b].Add(ac);
                });
            });

            // extract the assert expressions and add in the assume expressions
            var bestBlock = trueAsserts.Keys.First(b => trueAsserts[b].Count == trueAsserts.Max(p => p.Value.Count));
            bestBlock.Cmds.Iter(c =>
            {
                var ac = c as AssumeCmd;
                if (ac != null)
                {
                    assumeVars.Iter(t =>
                    {
                        if (t.Item1.ToString() == ac.Expr.ToString())
                        {
                            Console.WriteLine("// " + t.Item2 + " == " + t.Item3);
                        }
                    });

                }
            });
            Console.WriteLine("//   ==> ");
            var matchedVars = new HashSet<string>(); // remember that a variable can be matched more than once
            trueAsserts[bestBlock].Iter(a =>
            {
                var s = a.Expr.ToString().Split(new string[] { " || " }, StringSplitOptions.None);
                Debug.Assert(s.Count() > 1);
                Console.WriteLine("// " + s[1]);
                matchedVars.Add(s[1].Split(new string[] { " == " }, StringSplitOptions.None)[0]);
            });

            Console.WriteLine("\n// Percentage of Matched Locals = {0}%", 100 * matchedVars.Count / numQueryLocals);

        }

        private static string RunBoogie(string programFilename)
        {
            // TODO: check that boogie runs correctly
            // Start the child process.
            Process p = new Process
            {
                StartInfo =
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    FileName = "Boogie.exe",
                    Arguments = $"{programFilename} /errorLimit:{MaxAsserts} -useArrayTheory"
                }
            };
            // Redirect the output stream of the child process.
            p.Start();
            // Read the output stream first and then wait.
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return output;
        }

        private void JoinImplementations(Implementation queryImplementation, Implementation targetImplementation)
        {
            queryImplementation.InParams.AddRange(targetImplementation.InParams);
            queryImplementation.Proc.InParams.AddRange(targetImplementation.InParams);
            queryImplementation.LocVars.AddRange(targetImplementation.LocVars);
            queryImplementation.LocVars.Add(_havocVar);
            queryImplementation.Blocks.Last().TransferCmd = new GotoCmd(Token.NoToken,
                new List<Block>() { targetImplementation.Blocks.First() });
            queryImplementation.Blocks.AddRange(targetImplementation.Blocks);
        }

        private void JoinTopLevelDeclarations(Program queryProgram, Program targetProgram)
        {
            // add all of target's functions, constants, globals and procedures to query
            queryProgram.AddTopLevelDeclarations(targetProgram.Functions.Where(f2 => !queryProgram.Functions.Select(f => f.Name).Contains(f2.Name)));
            queryProgram.AddTopLevelDeclarations(targetProgram.Constants.Select(c2 => new Constant(Token.NoToken, new TypedIdent(Token.NoToken, _v2Prefix + c2.Name, c2.TypedIdent.Type), c2.Unique)));
            queryProgram.AddTopLevelDeclarations(targetProgram.GlobalVariables.Select(g2 => new GlobalVariable(Token.NoToken, new TypedIdent(Token.NoToken, _v2Prefix + g2.Name, g2.TypedIdent.Type))));
        }

        private List<Expr> CreateAssertsExprs(Implementation queryImplementation, Implementation targetImplementation)
        {
            var result = new List<Expr>();
            queryImplementation.LocVars.Iter(v =>
            {
                var type = Utils.BoogieUtils.GetExprType(Expr.Ident(v));
                if (type != null)
                    targetImplementation.LocVars.Iter(v2 =>
                    {
                        if (type.Equals(Utils.BoogieUtils.GetExprType(Expr.Ident(v2)))) result.Add(Expr.Eq(Expr.Ident(v), Expr.Ident(v2)));
                    });
            });
            return result;
        }

        private List<Block> CreateAssertsBlocks(Implementation queryImplementation, Implementation targetImplementation, List<Tuple<Variable, Expr, Expr>> assumeVars)
        {
            var exprs = CreateAssertsExprs(queryImplementation, targetImplementation);
            var assertsCmds = CreateAsserts(exprs);

            queryImplementation.InParams.Iter(iq =>
            {
                targetImplementation.InParams.Iter(it =>
                {
                    if (Equals(iq.TypedIdent.Type, it.TypedIdent.Type))
                    {
                        var eqVar = new LocalVariable(Token.NoToken, new TypedIdent(Token.NoToken, AssumeVarPrefix + "_" + _eqVarsCounter++, BType.Bool));
                        assumeVars.Add(new Tuple<Variable, Expr, Expr>(eqVar, Expr.Ident(iq), Expr.Ident(it)));
                        queryImplementation.Blocks[0].Cmds.Insert(0, Utils.BoogieUtils.CreateAssignCmd(new List<IdentifierExpr>() { Expr.Ident(eqVar) }, new List<Expr>() { Expr.Eq(Expr.Ident(iq), Expr.Ident(it)) }));
                    }
                });
            });

            // The equality vars are grouped according to lhs. This means that expressions like eq_0 = `rax == v2.rcx` and eq_13 = `rax == v2.p0` will be 
            // grouped together, to make the assumes choosing algorithm more efficient (we won't select eq_0 and eq_13 for the same section)
            var eqVarsGroups = new Dictionary<string, List<Tuple<Variable, Expr, Expr>>>();
            assumeVars.Iter(t =>
            {
                var lhs = t.Item2.ToString();
                if (!eqVarsGroups.ContainsKey(lhs))
                    eqVarsGroups[lhs] = new List<Tuple<Variable, Expr, Expr>>();
                eqVarsGroups[lhs].Add(t);
            });
            assumeVars.Iter(t => queryImplementation.LocVars.Add(t.Item1));
            return CreateAssertsWithAssumes(eqVarsGroups.Values.ToList(), assertsCmds);
        }

        private List<Block> CreateAssertsWithAssumes(List<List<Tuple<Variable, Expr, Expr>>> eqVarsGroups, List<Cmd> asserts)
        {
            int n = eqVarsGroups.Count();
            if (n == 0)
            {
                var b = new Block { Label = SectionLabelPrefix + "_" + _labelCounter++ };
                b.Cmds.AddRange(asserts);
                return new List<Block>() { b };
            }

            var result = new List<Block>();
            EnumerateAssumes(new List<Tuple<int, int>>(), n, asserts, eqVarsGroups, result);
            return result;
        }

        private List<Cmd> CreateAsserts(IEnumerable<Expr> exprs)
        {
            var result = new List<Cmd>();
            foreach (var e in exprs)
            {
                result.Add(new HavocCmd(Token.NoToken, new List<IdentifierExpr>() { Expr.Ident(_havocVar) }));
                result.Add(new AssertCmd(Token.NoToken, Expr.Or(Expr.Ident(_havocVar), e)));
            }
            // if the tracelets have conflicting assumes, everything can be proved.
            // add an 'assert false;' at the end, to check at this did not happen
            result.Add(new AssertCmd(Token.NoToken, Expr.False));
            return result.ToList();
        }

        private void EnumerateAssumes(List<Tuple<int, int>> pick, int depth, List<Cmd> asserts,
            List<List<Tuple<Variable, Expr, Expr>>> eqVarsGroups, List<Block> result)
        {
            // if we the number of asserts exceeds twice the maximum amount of asserts, the tracelet is hopeless, so generate empty output and don't try to solve
            if (_numOverallAsserts >= MaxAsserts)
            {
                // TODO: magic
                Utils.ErrorAndDie($"Sorry, can't handle very long programs (max {MaxAsserts} assertions allowed, {_numOverallAsserts} reached)");
            }

            if (depth == 0)
            {
                var usedExprs = new HashSet<string>();
                var eqVarsPick = new List<Variable>();
                foreach (Tuple<int, int> t in pick)
                {
                    var tuple = eqVarsGroups[t.Item1][t.Item2];
                    if (usedExprs.Contains(tuple.Item3.ToString()))
                        return;
                    usedExprs.Add(tuple.Item3.ToString());
                    eqVarsPick.Add(tuple.Item1);
                }

                var b = new Block { Label = SectionLabelPrefix + "_" + _labelCounter++ };
                foreach (var eqv in eqVarsPick)
                    b.Cmds.Add(new AssumeCmd(Token.NoToken, Expr.Ident(eqv)));
                b.Cmds.AddRange(asserts);
                b.TransferCmd = new ReturnCmd(Token.NoToken);
                result.Add(b);
                return;
            }

            // always start from the next group
            for (var i = (pick.Count > 0) ? pick.Last().Item1 + 1 : 0; i < eqVarsGroups.Count; ++i)
            {
                // inside the group, try all candidates
                for (int j = 0; j < eqVarsGroups[i].Count; ++j)
                {
                    var l = new List<Tuple<int, int>>(pick) { new Tuple<int, int>(i, j) };
                    EnumerateAssumes(l, depth - 1, asserts, eqVarsGroups, result);
                }
            }

        }

    }

}
