//
//  Copyright (c) Microsoft Corporation. All rights reserved.
//  Licensed under the MIT License. See License.txt in the project root for license information.
//
// This file contain implementations details that are subject to change without notice.
// Use at your own risk.
//
using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.Globalization;
using System.Collections;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Utilities;

namespace Microsoft.VisualStudio.Text.Implementation
{
    internal sealed class BinaryStringRebuilder : StringRebuilder
    {
        internal readonly StringRebuilder _left;
        internal readonly StringRebuilder _right;

#if DEBUG
        private static int _totalCreated = 0;
        public static int TotalCreated { get { return _totalCreated; } }
#endif

        #region Private
        private static StringRebuilder _crlf = StringRebuilder.Create("\r\n");

        // internal for unit tests, a \r\n can't be spanned by left and right
        internal BinaryStringRebuilder(StringRebuilder left, StringRebuilder right)
            : base(left.Length + right.Length, left.LineBreakCount + right.LineBreakCount, left.FirstCharacter, right.LastCharacter)
        {
            Debug.Assert(left.Length > 0);
            Debug.Assert(right.Length > 0);
            Debug.Assert(Math.Abs(left.Depth - right.Depth) <= 1);
            Debug.Assert(left.LastCharacter != '\r' || right.FirstCharacter != '\n');

#if DEBUG
            Interlocked.Increment(ref _totalCreated);
#endif

            _left = left;
            _right = right;
            this.Depth = 1 + Math.Max(left.Depth, right.Depth);
        }

        private static StringRebuilder ConsolidateOrBalanceTreeNode(StringRebuilder left, StringRebuilder right)
        {
            if ((left.Length + right.Length < TextModelOptions.StringRebuilderMaxCharactersToConsolidate) &&
                (left.LineBreakCount + right.LineBreakCount <= TextModelOptions.StringRebuilderMaxLinesToConsolidate))
            {
                //Consolidate the two rebuilders into a single simple string rebuilder
                return StringRebuilder.Consolidate(left, right);
            }
            else
                return BinaryStringRebuilder.BalanceTreeNode(left, right);
        }

        private static StringRebuilder BalanceStringRebuilder(StringRebuilder left, StringRebuilder right)
        {
            return BinaryStringRebuilder.BalanceTreeNode(left, right);
        }

        private static StringRebuilder BalanceTreeNode(StringRebuilder left, StringRebuilder right)
        {
            if (left.Depth > right.Depth + 1)
                return BinaryStringRebuilder.Pivot(left, right, false);
            else if (right.Depth > left.Depth + 1)
                return BinaryStringRebuilder.Pivot(right, left, true);
            else
                return new BinaryStringRebuilder(left, right);
        }

        private static StringRebuilder Pivot(StringRebuilder child, StringRebuilder other, bool deepOnRightSide)
        {
            Debug.Assert(child.Depth > 0);  //child's depth is greater than other's depth.
            StringRebuilder grandchildOutside = child.Child(deepOnRightSide);
            StringRebuilder grandchildInside = child.Child(!deepOnRightSide);

            if (grandchildOutside.Depth >= grandchildInside.Depth)
            {
                //Simple pivot.
                //From this (case deepOnRightSide)
                //                    this
                //                   /    \
                //                other    child
                //                 ...    /      \
                //                       gcI    gcO
                //                       ...    ...
                //
                //To this:
                //                    child'
                //                   /      \
                //                 this'      gcO
                //                /    \     ...
                //             other   gcI

                StringRebuilder newThis;
                StringRebuilder newChild;
                if (deepOnRightSide)
                {
                    newThis = BinaryStringRebuilder.ConsolidateOrBalanceTreeNode(other, grandchildInside);
                    newChild = BinaryStringRebuilder.ConsolidateOrBalanceTreeNode(newThis, grandchildOutside);
                }
                else
                {
                    newThis = BinaryStringRebuilder.ConsolidateOrBalanceTreeNode(grandchildInside, other);
                    newChild = BinaryStringRebuilder.ConsolidateOrBalanceTreeNode(grandchildOutside, newThis);
                }

                return newChild;
            }
            else
            {
                //Complex pivot.
                //From this (case !deepOnRightSide)
                //                    this
                //                   /    \
                //                other    child
                //                 ...    /      \
                //                       gcI      gcO
                //                     /     \    ...
                //                   ggcI   ggcO
                //                    ...    ...
                //
                //To this:
                //                     gcI'
                //                   /     \
                //                 this'     child'
                //                /    \    /     \
                //              other ggcI ggcO   gcO
                //              ...  ...   ...    ...
                Debug.Assert(grandchildInside.Depth > 0);  //The inside's grandchild depth is > the outside grandchild's.
                StringRebuilder greatgrandchildOutside = grandchildInside.Child(deepOnRightSide);
                StringRebuilder greatgrandchildInside = grandchildInside.Child(!deepOnRightSide);

                StringRebuilder newThis;
                StringRebuilder newChild;
                StringRebuilder newGcI;

                if (deepOnRightSide)
                {
                    newThis = BinaryStringRebuilder.ConsolidateOrBalanceTreeNode(other, greatgrandchildInside);
                    newChild = BinaryStringRebuilder.ConsolidateOrBalanceTreeNode(greatgrandchildOutside, grandchildOutside);
                    newGcI = BinaryStringRebuilder.ConsolidateOrBalanceTreeNode(newThis, newChild);
                }
                else
                {
                    newThis = BinaryStringRebuilder.ConsolidateOrBalanceTreeNode(greatgrandchildInside, other);
                    newChild = BinaryStringRebuilder.ConsolidateOrBalanceTreeNode(grandchildOutside, greatgrandchildOutside);
                    newGcI = BinaryStringRebuilder.ConsolidateOrBalanceTreeNode(newChild, newThis);
                }

                return newGcI;
            }
        }
        #endregion

        public static StringRebuilder Create(StringRebuilder left, StringRebuilder right)
        {
            if (left == null)
                throw new ArgumentNullException(nameof(left));
            if (right == null)
                throw new ArgumentNullException(nameof(right));

            if (left.Length == 0)
                return right;
            else if (right.Length == 0)
                return left;
            else if ((left.Length + right.Length < TextModelOptions.StringRebuilderMaxCharactersToConsolidate) &&
                    (left.LineBreakCount + right.LineBreakCount <= TextModelOptions.StringRebuilderMaxLinesToConsolidate))
            {
                //Consolidate the two rebuilders into a single simple string rebuilder
                return StringRebuilder.Consolidate(left, right);
            }
            else if ((right.FirstCharacter == '\n') && (left.LastCharacter == '\r'))
            {
                //Don't allow a line break to be broken across the seam
                return BinaryStringRebuilder.Create(BinaryStringRebuilder.Create(left.GetSubText(new Span(0, left.Length - 1)),
                                                                                 _crlf),
                                                    right.GetSubText(Span.FromBounds(1, right.Length)));
            }
            else
            {
                return BinaryStringRebuilder.BalanceStringRebuilder(left, right);
            }
        }

        public override string ToString()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, this.Depth % 2 == 0 ? "({0})({1})" : "[{0}][{1}]",
                                 _left.ToString(), _right.ToString());
        }

        public override int Depth { get; }

        #region StringRebuilder Members
        public override int GetLineNumberFromPosition(int position)
        {
            if ((position < 0) || (position > this.Length))
                throw new ArgumentOutOfRangeException(nameof(position));

            return (position <= _left.Length)
                   ? _left.GetLineNumberFromPosition(position)
                   : (_left.LineBreakCount +
                      _right.GetLineNumberFromPosition(position - _left.Length));
        }

        public override void GetLineFromLineNumber(int lineNumber, out Span extent, out int lineBreakLength)
        {
            if ((lineNumber < 0) || (lineNumber > this.LineBreakCount))
                throw new ArgumentOutOfRangeException(nameof(lineNumber));

            if (lineNumber < _left.LineBreakCount)
            {
                _left.GetLineFromLineNumber(lineNumber, out extent, out lineBreakLength);
            }
            else if (lineNumber > _left.LineBreakCount)
            {
                _right.GetLineFromLineNumber(lineNumber - _left.LineBreakCount, out extent, out lineBreakLength);
                extent = new Span(extent.Start + _left.Length, extent.Length);
            }
            else
            {
                // The line crosses the seam.
                int start = 0;
                if (lineNumber != 0)
                {
                    _left.GetLineFromLineNumber(lineNumber, out extent, out lineBreakLength);   // ignore the returned extend.Length

                    start = extent.Start;
                    Debug.Assert(lineBreakLength == 0);
                }

                int end;

                if (lineNumber == this.LineBreakCount)
                {
                    end = this.Length;
                    lineBreakLength = 0;
                }
                else
                {
                    _right.GetLineFromLineNumber(0, out extent, out lineBreakLength);
                    end = extent.End + _left.Length;
                }

                extent = Span.FromBounds(start, end);
            }
        }

        public override StringRebuilder GetLeaf(int position, out int offset)
        {
            if (position < _left.Length)
            {
                return _left.GetLeaf(position, out offset);
            }
            else
            {
                var leaf = _right.GetLeaf(position - _left.Length, out offset);
                offset += _left.Length;
                return leaf;
            }
        }

        public override char this[int index]
        {
            get
            {
                if ((index < 0) || (index >= this.Length))
                    throw new ArgumentOutOfRangeException(nameof(index));

                return (index < _left.Length)
                        ? _left[index]
                        : _right[index - _left.Length];
            }
        }

        public override string GetText(Span span)
        {
            if (span.End > this.Length)
                throw new ArgumentOutOfRangeException(nameof(span));

            if (span.End <= _left.Length)
                return _left.GetText(span);
            else if (span.Start >= _left.Length)
                return _right.GetText(new Span(span.Start - _left.Length, span.Length));
            else
            {
                char[] result = new char[span.Length];

                int leftLength = _left.Length - span.Start;
                _left.CopyTo(span.Start, result, 0, leftLength);
                _right.CopyTo(0, result, leftLength, span.Length - leftLength);

                return new string(result);
            }
        }

        public override void CopyTo(int sourceIndex, char[] destination, int destinationIndex, int count)
        {
            //These tests get executed a lot and are redundant: if there is an error, then the corresponding exception
            //will be thrown when we reach a leaf node.

            //if (sourceIndex < 0)
            //    throw new ArgumentOutOfRangeException("sourceIndex");
            //if (destination == null)
            //    throw new ArgumentNullException("destination");
            //if (destinationIndex < 0)
            //    throw new ArgumentOutOfRangeException("destinationIndex");
            //if (count < 0)
            //    throw new ArgumentOutOfRangeException("count");

            //if ((sourceIndex + count > this.Length) || (sourceIndex + count < 0))
            //    throw new ArgumentOutOfRangeException("count");

            //if ((destinationIndex + count > destination.Length) || (destinationIndex + count < 0))
            //    throw new ArgumentOutOfRangeException("count");

            if (sourceIndex >= _left.Length)
                _right.CopyTo(sourceIndex - _left.Length, destination, destinationIndex, count);
            else if (sourceIndex + count <= _left.Length)
                _left.CopyTo(sourceIndex, destination, destinationIndex, count);
            else
            {
                int leftLength = _left.Length - sourceIndex;

                _left.CopyTo(sourceIndex, destination, destinationIndex, leftLength);
                _right.CopyTo(0, destination, destinationIndex + leftLength, count - leftLength);
            }
        }

        public override void Write(TextWriter writer, Span span)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (span.End > this.Length)
                throw new ArgumentOutOfRangeException(nameof(span));

            if (span.Start >= _left.Length)
                _right.Write(writer, new Span(span.Start - _left.Length, span.Length));
            else if (span.End <= _left.Length)
                _left.Write(writer, span);
            else
            {
                _left.Write(writer, Span.FromBounds(span.Start, _left.Length));
                _right.Write(writer, Span.FromBounds(0, span.End - _left.Length));
            }
        }

        public override StringRebuilder GetSubText(Span span)
        {
            if (span.End > this.Length)
                throw new ArgumentOutOfRangeException(nameof(span));

            if (span.Length == this.Length)
                return this;
            else if (span.End <= _left.Length)
                return _left.GetSubText(span);
            else if (span.Start >= _left.Length)
                return _right.GetSubText(new Span(span.Start - _left.Length, span.Length));
            else
                return BinaryStringRebuilder.Create(_left.GetSubText(Span.FromBounds(span.Start, _left.Length)),
                                                    _right.GetSubText(Span.FromBounds(0, span.End - _left.Length)));
        }

        public override StringRebuilder Child(bool rightSide)
        {
            return rightSide ? _right : _left;
        }
        #endregion
    }
}
