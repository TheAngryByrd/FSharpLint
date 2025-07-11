module FSharpLint.Rules.FailwithBadUsage

open FSharpLint.Framework
open FSharpLint.Framework.Suggestion
open FSharp.Compiler.Syntax
open FSharpLint.Framework.Ast
open FSharpLint.Framework.Rules
open System
open System.Collections.Generic

type Location =
    {
        FileName: string
        StartLine: int
        StartColumn: int
    }

let mutable failwithMessages = Set.empty

type private BadUsageType =
    | EmptyMessage
    | DuplicateMessage
    | SwallowedException
    | NullMessage

let private runner (args: AstNodeRuleParams) =
    let generateError
        failwithKeyword
        failwithErrorMessage
        range
        (badUsageType: BadUsageType)
        (exceptionParam: Option<string>)
        =
        let suggestedFix =
            match exceptionParam with
            | Some param ->
                Some(
                    lazy
                        (Some
                            { FromText = $"%s{failwithKeyword} %s{failwithErrorMessage}"
                              FromRange = range
                              ToText = $"raise <| Exception(\"%s{failwithErrorMessage}\", %s{param})" })
                )
            | _ -> None

        let message =
            match badUsageType with
            | EmptyMessage -> "consider using a non-empty error message as parameter"
            | DuplicateMessage -> "consider using unique error messages as parameters"
            | SwallowedException ->
                "rather use `raise` passing the current exception as innerException (2nd parameter of Exception constructor), otherwise using `failwith` the exception details will be swallowed"
            | NullMessage -> "consider using a non-null error messages as parameter"

        let error =
            { Range = range
              Message = String.Format(Resources.GetString "RulesFailwithBadUsage", message)
              SuggestedFix = suggestedFix
              TypeChecks = List.Empty }
            |> Array.singleton

        error

    /// Error message generated by F# compiler in place of extern declaration
    let fakeExternDeclErrorMsg = "extern was not given a DllImport attribute"

    let rec checkExpr node maybeIdentifier =
        match node with
        | SynExpr.App (_, _, SynExpr.Ident failwithId, expression, range) when
            failwithId.idText = "failwith"
            || failwithId.idText = "failwithf"
            ->
            match expression with
            | SynExpr.Const (SynConst.String (id, _, _), _) when id = "" ->
                generateError failwithId.idText id range BadUsageType.EmptyMessage maybeIdentifier
            | SynExpr.Const (SynConst.String (id, _, _), _) when id <> fakeExternDeclErrorMsg ->
                let isDuplicate =
                    let location =
                        { FileName = range.FileName
                          StartLine = range.StartLine
                          StartColumn = range.StartColumn }

                    Set.exists
                        (fun (message, failwithMsgLocation) ->
                            id = message
                            && location <> failwithMsgLocation)
                        failwithMessages

                if isDuplicate then
                    generateError failwithId.idText id range BadUsageType.DuplicateMessage maybeIdentifier
                else
                    match maybeIdentifier with
                    | Some maybeId ->
                        generateError failwithId.idText id range BadUsageType.SwallowedException (Some maybeId)
                    | _ ->
                        failwithMessages <-
                            failwithMessages.Add(id, { FileName = range.FileName; StartLine = range.StartLine; StartColumn = range.StartColumn })

                        Array.empty
            | SynExpr.LongIdent (_, SynLongIdent (id, _, _), _, _) when
                (ExpressionUtilities.longIdentToString id) = "String.Empty"
                || (ExpressionUtilities.longIdentToString id) = "System.String.Empty"
                ->
                generateError
                    failwithId.idText
                    (ExpressionUtilities.longIdentToString id)
                    range
                    (BadUsageType.EmptyMessage)
                    (None)
            | SynExpr.Null range ->
                generateError failwithId.idText "null" range BadUsageType.NullMessage maybeIdentifier
            | _ -> Array.empty
        | SynExpr.TryWith (_, clauseList, _expression, _range, _, _) ->
            clauseList
            |> List.toArray
            |> Array.collect (fun clause ->
                match clause with
                | SynMatchClause (pat, _, app, _, _, _) ->
                    match pat with
                    | SynPat.Named (SynIdent(id, _), _, _, _) -> checkExpr app (Some id.idText)
                    | _ -> checkExpr app None)
                | _ -> Array.empty

    match args.AstNode with
    | AstNode.Expression expr -> checkExpr expr None
    | _ -> Array.empty

let cleanup () = failwithMessages <- Set.empty

let rule =
    { Name = "FailwithBadUsage"
      Identifier = Identifiers.FailwithBadUsage
      RuleConfig =
        { AstNodeRuleConfig.Runner = runner
          Cleanup = cleanup } }
    |> AstNodeRule
