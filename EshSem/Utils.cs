using System.Collections.Generic;
using System.Linq;
using Microsoft.Boogie;
using Type = Microsoft.Boogie.Type;
using System.Diagnostics;
using System;
using System.IO;

namespace EshSem
{
    class Utils
    {
        public static void ErrorAndDie(string error)
        {
            string execName = Process.GetCurrentProcess().MainModule.ModuleName;
            Console.Error.WriteLine($"ERROR({execName}): {error}");
            Environment.Exit(-1);
        }
        public static void Warning(string error)
        {
            string execName = Process.GetCurrentProcess().MainModule.ModuleName;
            Console.Error.WriteLine($"WARNING({execName}): {error}");
        }

        public static void SetupBoogie()
        {
            CommandLineOptions.Install(new CommandLineOptions()); // this is required for parsing the program with boogie
            CommandLineOptions.Clo.RunningBoogieFromCommandLine = true;
            var boogieOptions = "/typeEncoding:m -timeLimit:20 -removeEmptyBlocks:0 /printModel:1 /printModelToFile:model.dmp /printInstrumented ";
            CommandLineOptions.Clo.Parse(boogieOptions.Split(' '));
            CommandLineOptions.Clo.UseUnsatCoreForContractInfer = true;
            CommandLineOptions.Clo.ContractInfer = true;
            CommandLineOptions.Clo.ExplainHoudini = true;
        }
        
        public static bool ParseProgram(string path, out Program program)
        {
            program = null;
            int errCount;
            try
            {
                errCount = Parser.Parse(path, new List<string>(), out program);
                if (errCount != 0 || program == null)
                {
                    Warning($"Parse errors detected in {path}");
                    return false;
                }
            }
            catch (IOException e)
            {
                Warning($"Error opening file \"{path}\": {e.Message}");
                return false;
            }
            errCount = program.Resolve();
            if (errCount > 0)
            {
                Warning($"Name resolution errors in {path}");
                return false;
            }
            ModSetCollector c = new ModSetCollector();
            c.DoModSetAnalysis(program);

            return true;
        }

        public static void PrintProgram(Program program, string path = null)
        {
            var ttw = path == null ? new TokenTextWriter(Console.Out, true) : new TokenTextWriter(path, true);
            program.Emit(ttw);
            ttw.Close();
            // re-read to see there are no parse errors
            Program writtenProgram;
            if (path != null && !ParseProgram(path, out writtenProgram))
                ErrorAndDie($"Created program {path} with parse errors.");

        }

        public class VarRenamer : StandardVisitor
        {
            private readonly string _prefix;
            public List<string> Ignore;
            public VarRenamer(string prefix, string[] ignore)
            {
                _prefix = prefix;
                Ignore = new List<string>(ignore);
            }

            public override Implementation VisitImplementation(Implementation node)
            {
                var result = base.VisitImplementation(node);
                result.Name = _prefix + result.Name;
                result.InParams = result.InParams.Select(i => new LocalVariable(Token.NoToken, new TypedIdent(Token.NoToken, i.Name, i.TypedIdent.Type)) as Variable).ToList();
                result.OutParams = result.OutParams.Select(o => new LocalVariable(Token.NoToken, new TypedIdent(Token.NoToken, o.Name, o.TypedIdent.Type)) as Variable).ToList();
                result.LocVars = result.LocVars.Select(v => new LocalVariable(Token.NoToken, new TypedIdent(Token.NoToken, v.Name, v.TypedIdent.Type)) as Variable).ToList();
                return result;
            }

            public override Expr VisitNAryExpr(NAryExpr node)
            {
                node.Args = node.Args.Select(arg => VisitExpr(arg)).ToList();
                return node;
            }

            public override Variable VisitVariable(Variable node)
            {
                if (node.Name.StartsWith(_prefix))
                    return node;
                var result = node.Clone() as Variable;
                if (result == null)
                    return node;
                result.Name = _prefix + result.Name;
                return result;
            }

            public override Expr VisitIdentifierExpr(IdentifierExpr node)
            {
                var result = new IdentifierExpr(Token.NoToken, VisitVariable(node.Decl));
                return result;
            }

            public override Cmd VisitCallCmd(CallCmd node)
            {
                if (!Ignore.Any(p => node.callee.StartsWith(p)))
                    node.callee = _prefix + node.callee;
                return base.VisitCallCmd(node);
            }

            public override Cmd VisitAssignCmd(AssignCmd node)
            {
                var renamedLhss = new List<AssignLhs>();
                foreach (var l in node.Lhss)
                {
                    if (l is SimpleAssignLhs)
                    {
                        renamedLhss.Add(new SimpleAssignLhs(Token.NoToken, Expr.Ident(VisitVariable(l.DeepAssignedVariable))));
                    }
                    else if (l is MapAssignLhs)
                    {
                        var mal = l as MapAssignLhs;
                        renamedLhss.Add(new MapAssignLhs(Token.NoToken,
                            new SimpleAssignLhs(Token.NoToken, VisitIdentifierExpr(mal.DeepAssignedIdentifier) as IdentifierExpr),
                            mal.Indexes.Select(i => VisitExpr(i)).ToList()));
                    }
                    else
                    {
                        ErrorAndDie("Unknown assign type");
                    }
                }

                var renamedRhss = new List<Expr>();
                node.Rhss.Iter(r => renamedRhss.Add(VisitExpr(r)));

                var result = new AssignCmd(Token.NoToken, renamedLhss, renamedRhss);
                return result;
            }

            public override Block VisitBlock(Block node)
            {
                node.Label = _prefix + node.Label;
                var gotoCmd = node.TransferCmd as GotoCmd;
                if (gotoCmd != null)
                    node.TransferCmd = new GotoCmd(Token.NoToken, gotoCmd.labelNames.Select(l => _prefix + l).ToList());
                return base.VisitBlock(node);
            }
        }


        public class BoogieUtils
        {

            public static string GetExprType(Expr expr)
            {
                var le = expr as LiteralExpr;
                if (le != null)
                    return le.Type.ToString();
                var ie = expr as IdentifierExpr;
                if (ie != null)
                    return ie.Decl.TypedIdent.Type.ToString();
                var ne = expr as NAryExpr;
                if (ne != null && ne.Fun is MapSelect)
                    return ((ne.Args[0] as IdentifierExpr).Decl.TypedIdent.Type as MapType).Result.ToString();
                return null;
            }

            public static AssignCmd CreateAssignCmd(IEnumerable<IdentifierExpr> lhs, IEnumerable<Expr> rhs)
            {
                List<AssignLhs> assignLhss = new List<AssignLhs>();
                lhs.Iter(i => assignLhss.Add(new SimpleAssignLhs(Token.NoToken, i)));
                return new AssignCmd(new Token(), assignLhss, rhs.ToList());
            }
        }
        }
}
