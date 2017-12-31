﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

namespace Microsoft.CodeAnalysis.RegularExpressions
{
    internal enum RegexKind
    {
        None,
        EndOfFile,
        Sequence,
        CompilationUnit,
        Text,
        StartAnchor,
        EndAnchor,
        Alternation,
        Wildcard,
        CharacterClass,
        NegatedCharacterClass,
        CharacterClassRange,
        CharacterClassSubtraction,
        PosixProperty,

        ZeroOrMoreQuantifier,
        OneOrMoreQuantifier,
        ZeroOrOneQuantifier,
        ExactNumericQuantifier,
        OpenRangeNumericQuantifier,
        ClosedRangeNumericQuantifier,
        LazyQuantifier,

        SimpleGrouping,
        SimpleOptionsGrouping,
        NestedOptionsGrouping,
        NonCapturingGrouping,
        PositiveLookaheadGrouping,
        NegativeLookaheadGrouping,
        PositiveLookbehindGrouping,
        NegativeLookbehindGrouping,
        NonBacktrackingGrouping,
        CaptureGrouping,
        BalancingGrouping,
        ConditionalCaptureGrouping,
        ConditionalExpressionGrouping,

        SimpleEscape,
        CategoryEscape,
        ControlEscape,
        HexEscape,
        UnicodeEscape,
        OctalEscape,
        CaptureEscape,
        KCaptureEscape,
        BackreferenceEscape,

        // Tokens
        DollarToken,
        OpenBraceToken,
        CloseBraceToken,
        OpenBracketToken,
        CloseBracketToken,
        OpenParenToken,
        CloseParenToken,
        BarToken,
        DotToken,
        CaretToken,
        TextToken,
        QuestionToken,
        AsteriskToken,
        PlusToken,
        CommaToken,
        BackslashToken,
        ColonToken,
        EqualsToken,
        ExclamationToken,
        GreaterThanToken,
        LessThanToken,
        MinusToken,
        QuoteToken,

        // Special multi-character tokens that have to be explicitly requested.
        OptionsToken,
        NumberToken,
        CaptureNameToken,
        EscapeCategoryToken,

        // Trivia
        CommentTrivia,
        WhitespaceTrivia,
    }
}
