﻿// Copyright (c) Alexandre Mutel. All rights reserved.
// This file is licensed under the BSD-Clause 2 license. 
// See the license.txt file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Textamina.Markdig.Helpers;
using Textamina.Markdig.Syntax;

namespace Textamina.Markdig.Parsers
{
    /// <summary>
    /// The <see cref="BlockParser"/> state used by all <see cref="BlockParser"/>.
    /// </summary>
    public class BlockParserState
    {
        private int currentStackIndex;

        /// <summary>
        /// Initializes a new instance of the <see cref="BlockParserState"/> class.
        /// </summary>
        /// <param name="stringBuilders">The string builders cache.</param>
        /// <param name="document">The document to build blocks into.</param>
        /// <param name="parsers">The list of parsers.</param>
        /// <exception cref="System.ArgumentNullException">
        /// </exception>
        public BlockParserState(StringBuilderCache stringBuilders, Document document, BlockParserList parsers)
        {
            if (stringBuilders == null) throw new ArgumentNullException(nameof(stringBuilders));
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (parsers == null) throw new ArgumentNullException(nameof(parsers));
            StringBuilders = stringBuilders;
            Document = document;
            NewBlocks = new Stack<Block>();
            document.IsOpen = true;
            Stack = new List<Block> {document};
            Parsers = parsers;
            parsers.Initialize(this);
        }

        public List<Block> Stack { get; }

        public Stack<Block> NewBlocks { get; }

        public BlockParserList Parsers { get; }

        public ContainerBlock CurrentContainer { get; private set; }

        public Block LastBlock { get; private set; }

        public Block NextContinue => currentStackIndex + 1 < Stack.Count ? Stack[currentStackIndex + 1] : null;

        public Document Document { get; }

        public bool ContinueProcessingLine { get; set; }

        public StringSlice Line;

        public int LineIndex { get; private set; }

        public bool IsBlankLine => CurrentChar == '\0';

        public bool IsEndOfLine => Line.IsEmpty;

        public char CurrentChar => Line.CurrentChar;

        public char NextChar()
        {
            var c = Line.CurrentChar;
            if (c == '\t')
            {
                Column = CharHelper.AddTab(Column);
            }
            else
            {
                Column++;
            }
            return Line.NextChar();
        }

        public void NextColumn()
        {
            var c = Line.CurrentChar;
            // If we are accross a tab, we should just add 1 column
            if (c == '\t' && CharHelper.IsAcrossTab(Column))
            {
                Column++;
            }
            else
            {
                Line.NextChar();
                Column++;
            }
        }

        public char CharAt(int index) => Line[index];

        public int Start => Line.Start;

        public int EndOffset => Line.End;

        public int Indent => Column - ColumnBeforeIndent;

        public bool IsCodeIndent => Indent >= 4;

        public int ColumnBeforeIndent { get; private set; }

        public int StartBeforeIndent { get; private set; }

        public int Column { get; set; }

        public StringBuilderCache StringBuilders { get; }

        public char PeekChar(int offset)
        {
            return Line.PeekChar(offset);
        }

        private void ResetLine(StringSlice newLine)
        {
            Line = newLine;
            Column = 0;
            ColumnBeforeIndent = 0;
            StartBeforeIndent = 0;
        }

        public void RestartIndent()
        {
            StartBeforeIndent = Start;
            ColumnBeforeIndent = Column;
        }

        public void ParseIndent()
        {
            var c = CurrentChar;
            var previousStartBeforeIndent = StartBeforeIndent;
            var startBeforeIndent = Start;
            var previousColumnBeforeIndent = ColumnBeforeIndent;
            var columnBeforeIndent = Column;
            while (c !='\0')
            {
                if (c == '\t')
                {
                    Column = CharHelper.AddTab(Column);
                }
                else if (c == ' ')
                {
                    Column++;
                }
                else
                {
                    break;
                }
                c = Line.NextChar();
            }
            if (columnBeforeIndent == Column)
            {
                StartBeforeIndent = previousStartBeforeIndent;
                ColumnBeforeIndent = previousColumnBeforeIndent;
            }
            else
            {
                StartBeforeIndent = startBeforeIndent;
                ColumnBeforeIndent = columnBeforeIndent;
            }
        }

        public void ResetToColumn(int newColumn)
        {
            // Optimized path when we are moving above the previous start of indent
            if (newColumn > ColumnBeforeIndent)
            {
                Line.Start = StartBeforeIndent;
                Column = ColumnBeforeIndent;
                ColumnBeforeIndent = 0;
                StartBeforeIndent = 0;
            }
            else
            {
                Line.Start = 0;
                Column = 0;
                ColumnBeforeIndent = 0;
                StartBeforeIndent = 0;
            }
            for (; Line.Start <= Line.End && Column < newColumn; Line.Start++)
            {
                var c = Line.Text[Line.Start];
                if (c == '\t')
                {
                    Column = CharHelper.AddTab(Column);
                }
                else
                {
                    if (!c.IsSpaceOrTab())
                    {
                        ColumnBeforeIndent = Column + 1;
                        StartBeforeIndent = Line.Start + 1;
                    }

                    Column++;
                }
            }
            if (Column > newColumn)
            {
                Column = newColumn;
                if (Line.Start > 0)
                {
                    Line.Start--;
                }
            }
        }

        public void ResetToCodeIndent(int columnOffset = 0)
        {
            ResetToColumn(ColumnBeforeIndent + 4 + columnOffset);
        }

        public void Close(Block block)
        {
            // If we close a block, we close all blocks above
            for (int i = Stack.Count - 1; i >= 1; i--)
            {
                if (Stack[i] == block)
                {
                    for (int j = Stack.Count - 1; j >= i; j--)
                    {
                        Close(j);
                    }
                    break;
                }
            }
        }

        public void Discard(Block block)
        {
            for (int i = Stack.Count - 1; i >= 1; i--)
            {
                if (Stack[i] == block)
                {
                    block.Parent.Children.Remove(block);
                    Stack.RemoveAt(i);
                    break;
                }
            }
        }

        public void Close(int index)
        {
            var block = Stack[index];
            // If the pending object is removed, we need to remove it from the parent container
            if (!block.Parser.Close(this, block))
            {
                block.Parent?.Children.Remove(block);
            }
            Stack.RemoveAt(index);
        }

        public void CloseAll(bool force)
        {
            // Close any previous blocks not opened
            for (int i = Stack.Count - 1; i >= 1; i--)
            {
                var block = Stack[i];

                // Stop on the first open block
                if (!force && block.IsOpen)
                {
                    break;
                }
                Close(i);
            }
            UpdateLast(-1);
        }

        public void ProcessLine(string newLine)
        {
            ContinueProcessingLine = true;

            ResetLine(new StringSlice(newLine));
            LineIndex++;

            TryContinueBlocks();

            // If the line was not entirely processed by pending blocks, try to process it with any new block
            TryOpenBlocks();

            // Close blocks that are no longer opened
            CloseAll(false);
        }

        private void OpenAll()
        {
            for (int i = 1; i < Stack.Count; i++)
            {
                Stack[i].IsOpen = true;
            }
        }

        internal void UpdateLast(int stackIndex)
        {
            currentStackIndex = stackIndex < 0 ? Stack.Count - 1 : stackIndex;
            LastBlock = null;
            for (int i = Stack.Count - 1; i >= 0; i--)
            {
                var block = Stack[i];
                if (LastBlock == null)
                {
                    LastBlock = block;
                }

                var container = block as ContainerBlock;
                if (container != null)
                {
                    CurrentContainer = container;
                    break;
                }
            }
        }

        private void TryContinueBlocks()
        {
            // Set all blocks non opened. 
            // They will be marked as open in the following loop
            for (int i = 1; i < Stack.Count; i++)
            {
                Stack[i].IsOpen = false;
            }

            // Process any current block potentially opened
            for (int i = 1; i < Stack.Count; i++)
            {
                var block = Stack[i];

                ParseIndent();

                // If we have a paragraph block, we want to try to match other blocks before trying the Paragraph
                if (block is ParagraphBlock)
                {
                    break;
                }

                // Else tries to match the Default with the current line
                var parser = block.Parser;


                // If we have a discard, we can remove it from the current state
                UpdateLast(i);
                var result = parser.TryContinue(this, block);
                if (result == BlockState.Skip)
                {
                    continue;
                }

                if (result == BlockState.None)
                {
                    break;
                }

                RestartIndent();

                // In case the BlockParser has modified the blockParserState we are iterating on
                if (i >= Stack.Count)
                {
                    i = Stack.Count - 1;
                }

                // If a parser is adding a block, it must be the last of the list
                if ((i + 1) < Stack.Count && NewBlocks.Count > 0)
                {
                    throw new InvalidOperationException("A pending parser cannot add a new block when it is not the last pending block");
                }

                // If we have a leaf block
                var leaf = block as LeafBlock;
                if (leaf != null && NewBlocks.Count == 0)
                {
                    ContinueProcessingLine = false;
                    if (!result.IsDiscard())
                    {
                        leaf.AppendLine(ref Line, Column, LineIndex);
                    }

                    if (NewBlocks.Count > 0)
                    {
                        throw new InvalidOperationException(
                            "The NewBlocks is not empty. This is happening if a LeafBlock is not the last to be pushed");
                    }
                }

                // A block is open only if it has a Continue state.
                // otherwise it is a Break state, and we don't keep it opened
                block.IsOpen = result == BlockState.Continue || result == BlockState.ContinueDiscard;

                if (result == BlockState.BreakDiscard)
                {
                    ContinueProcessingLine = false;
                    break;
                }

                bool isLast = i == Stack.Count - 1;
                if (ContinueProcessingLine)
                {
                    ProcessNewBlocks(result, false);
                }
                if (isLast || !ContinueProcessingLine)
                {
                    break;
                }
            }
        }

        private void TryOpenBlocks()
        {
            while (ContinueProcessingLine)
            {
                // Eat indent spaces before checking the character
                ParseIndent();

                var parsers = Parsers.GetParsersForOpeningCharacter(CurrentChar);
                var globalParsers = Parsers.GlobalParsers;

                if (parsers != null)
                {
                    if (TryOpenBlocks(parsers))
                    {
                        RestartIndent();
                        continue;
                    }
                }

                if (globalParsers != null && ContinueProcessingLine)
                {
                    if (TryOpenBlocks(globalParsers))
                    {
                        RestartIndent();
                        continue;
                    }
                }

                break;
            }
        }

        private bool TryOpenBlocks(BlockParser[] parsers)
        {
            for (int j = 0; j < parsers.Length; j++)
            {
                var blockParser = parsers[j];
                if (Line.IsEmpty)
                {
                    ContinueProcessingLine = false;
                    break;
                }

                // UpdateLast the state of LastBlock and LastContainer
                UpdateLast(-1);

                // If a block parser cannot interrupt a paragraph, and the last block is a paragraph
                // we can skip this parser

                var lastBlock = LastBlock;
                if (!blockParser.CanInterrupt(this, lastBlock))
                {
                    continue;
                }

                bool isLazyParagraph = blockParser is ParagraphBlockParser && lastBlock is ParagraphBlock;

                var result = isLazyParagraph
                    ? blockParser.TryContinue(this, lastBlock)
                    : blockParser.TryOpen(this);

                if (result == BlockState.None)
                {
                    // If we have reached a blank line after trying to parse a paragraph
                    // we can ignore it
                    if (isLazyParagraph && IsBlankLine)
                    {
                        ContinueProcessingLine = false;
                        break;
                    }
                    continue;
                }

                // Special case for paragraph
                UpdateLast(-1);

                var paragraph = LastBlock as ParagraphBlock;
                if (isLazyParagraph && paragraph != null)
                {
                    Debug.Assert(NewBlocks.Count == 0);

                    if (!result.IsDiscard())
                    {
                        paragraph.AppendLine(ref Line, Column, LineIndex);
                    }

                    // We have just found a lazy continuation for a paragraph, early exit
                    // Mark all block opened after a lazy continuation
                    OpenAll();

                    ContinueProcessingLine = false;
                    break;
                }

                // Nothing found but the BlockParser may instruct to break, so early exit
                if (NewBlocks.Count == 0 && result == BlockState.BreakDiscard)
                {
                    ContinueProcessingLine = false;
                    break;
                }

                // If we have a container, we can retry to match against all types of block.
                ProcessNewBlocks(result, true);
                return ContinueProcessingLine;

                // We have a leaf node, we can stop
            }
            return false;
        }

        private void ProcessNewBlocks(BlockState result, bool allowClosing)
        {
            var newBlocks = NewBlocks;
            while (newBlocks.Count > 0)
            {
                var block = newBlocks.Pop();

                block.Line = LineIndex;

                // If we have a leaf block
                var leaf = block as LeafBlock;
                if (leaf != null)
                {
                    if (!result.IsDiscard())
                    {
                        leaf.AppendLine(ref Line, Column, LineIndex);
                    }

                    if (newBlocks.Count > 0)
                    {
                        throw new InvalidOperationException(
                            "The NewBlocks is not empty. This is happening if a LeafBlock is not the last to be pushed");
                    }
                }

                if (allowClosing)
                {
                    // Close any previous blocks not opened
                    CloseAll(false);
                }

                // If previous block is a container, add the new block as a children of the previous block
                if (block.Parent == null)
                {
                    UpdateLast(-1);
                    CurrentContainer.Children.Add(block);
                    block.Parent = CurrentContainer;
                }

                block.IsOpen = result.IsContinue();

                // Add a block blockParserState to the stack (and leave it opened)
                Stack.Add(block);

                if (leaf != null)
                {
                    ContinueProcessingLine = false;
                    return;
                }
            }
            ContinueProcessingLine = true;
        }
    }
}