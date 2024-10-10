using System;
using System.Collections;
using System.Collections.Generic;

namespace SimFS
{
    public class RangeList : IEnumerable<SimpleRange>
    {
        public class RangeComparer : IComparer<SimpleRange>
        {
            public static RangeComparer Default { get; } = new();
            public int Compare(SimpleRange x, SimpleRange y)
            {
                return x.start - y.start;
            }
        }

        public enum AddResult
        {
            Created,
            Merged,
            AlreadyMerged
        }

        public enum RemoveResult
        {
            NoChange,
            Removed,
            Shrinked,
            Splited,
        }

        private readonly SortedList<SimpleRange> _ranges = new(RangeComparer.Default);
        private Action<int, int> _onRangeDestruct;
        private Action<int, int> _onRangeRebuild;

        public int Count => _ranges.Count;

        public SimpleRange this[int index] => _ranges[index];

        public void SetCallbacks(Action<int, int> onRangeDestruct, Action<int, int> onRangeRebuild)
        {
            _onRangeDestruct = onRangeDestruct;
            _onRangeRebuild = onRangeRebuild;
        }

        public AddResult AddRange(int start, int length)
        {
            var _ = int.MaxValue;
            return AddRange(start, length, ref _);
        }

        public AddResult AddRange(int start, int length, ref int outerBoundary)
        {
            var merged = false;
            var searchItem = new SimpleRange(start, length);
            var index = _ranges.IndexOf(searchItem, false);
            if (index >= 0)
                return AddResult.AlreadyMerged;
            index = ~index;
            SimpleRange tempFrag = default;
            var prevIndex = index - 1;
            if (prevIndex >= 0)
            {
                var (prevStart, prevLength) = _ranges[prevIndex];
                var diff = prevStart + prevLength - start;
                if (diff > 0)
                    return AddResult.AlreadyMerged;
                //throw new SimFSException(ExceptionType.BitmapMessedUp, $"{start}~{start + length} is inside a fragment: {prevStart}~{prevStart + prevLength}");
                else if (diff == 0)
                {
                    _onRangeDestruct?.Invoke(prevStart, prevLength);
                    start = prevStart;
                    length += prevLength;
                    if (index >= _ranges.Count && CheckMergeWithNonFragIndex(ref outerBoundary))
                    {
                        _ranges.RemoveAt(prevIndex);
                    }
                    else
                    {
                        _ranges.DangerouslySetIndex(prevIndex, new SimpleRange(start, length));
                        tempFrag = new SimpleRange(start, length);
                    }
                    merged = true;
                }
            }
            if (index < _ranges.Count)
            {
                var (nextStart, nextLength) = _ranges[index];
                var diff = nextStart - start - length;
                if (diff < 0)
                    return AddResult.AlreadyMerged;
                //throw new SimFSException(ExceptionType.BitmapMessedUp, $"{start}~{start + length} is inside a fragment: {nextStart}~{nextStart + nextLength}");
                else if (diff == 0)
                {
                    _onRangeDestruct?.Invoke(nextStart, nextLength);
                    length += nextLength;
                    var endCheck = start + length;
                    if (CheckMergeWithNonFragIndex(ref outerBoundary))
                    {
                        _ranges.RemoveAt(index);
                    }
                    else
                    {
                        _ranges.DangerouslySetIndex(index, new SimpleRange(start, length));
                        _onRangeRebuild?.Invoke(start, length);
                    }
                    if (merged)
                    {
                        tempFrag = default;
                        _ranges.RemoveAt(prevIndex);
                    }
                    else
                        merged = true;
                }
            }

            if (!merged && !CheckMergeWithNonFragIndex(ref outerBoundary))
            {
                _ranges.Add(new SimpleRange(start, length));
                _onRangeRebuild?.Invoke(start, length);
                return AddResult.Created;
            }
            else if (tempFrag.length > 0)
            {
                _onRangeRebuild?.Invoke(tempFrag.start, tempFrag.length);
            }

            return AddResult.Merged;

            bool CheckMergeWithNonFragIndex(ref int bound)
            {
                var result = (start + length - bound) switch
                {
                    > 0 => throw new InvalidOperationException($"the range: {start}~{start + length} is outside the boundary"),
                    0 => true,
                    _ => false
                };
                if (result)
                    bound = start;
                return result;
            }
        }

        public AddResult AddRange(SimpleRange range) => AddRange(range.start, range.length);

        public AddResult AddRange(SimpleRange range, ref int outerBoundary) => AddRange(range.start, range.length, ref outerBoundary);

        public RemoveResult RemoveRange(int start, int length)
        {
            var index = _ranges.IndexOf(new SimpleRange(start, 0), false);
            if (index < 0)
                index = ~index;
            if (index >= _ranges.Count)
                return RemoveResult.NoChange;
            if (index + 1 < _ranges.Count)
            {
                var nextRange = _ranges[index + 1];
                if (start + length >= nextRange.start)
                {
                    throw new NotSupportedException("cross range remove is not supported");
                }
            }
            var prevRange = _ranges[index];
            if (prevRange.start <= start && prevRange.start + prevRange.length > start)
            {
                _ranges.RemoveAt(index);
                _onRangeDestruct?.Invoke(prevRange.start, prevRange.length);
                var newStart = prevRange.start;
                var newLength = start - newStart;
                var addCount = 0;
                if (newLength > 0)
                {
                    _ranges.Add(new SimpleRange(newStart, newLength));
                    _onRangeRebuild?.Invoke(newStart, newLength);
                    addCount++;
                }
                if (start + length < prevRange.start + prevRange.length)
                {
                    var newStart1 = start + length;
                    var newLength1 = prevRange.start + prevRange.length - newStart1;
                    _ranges.Add(new SimpleRange(newStart1, newLength1));
                    _onRangeRebuild?.Invoke(newStart1, newLength1);
                    addCount++;
                }
                return addCount switch
                {
                    0 => RemoveResult.Removed,
                    1 => RemoveResult.Shrinked,
                    2 => RemoveResult.Splited,
                    _ => throw new InvalidOperationException()
                };
            }
            return RemoveResult.NoChange;
        }

        public RemoveResult RemoveRange(SimpleRange range) => RemoveRange(range.start, range.length);

        public bool IsInRange(int x)
        {
            var index = _ranges.IndexOf(new SimpleRange(x, 0), false);
            if (index < 0)
                index = ~index;
            if (index > _ranges.Count)
                return false;
            var prevRange = _ranges[index];
            return prevRange.start <= x && prevRange.start + prevRange.length > x;
        }

        public int IndexOf(int start) => _ranges.IndexOf(new SimpleRange(start, 0), false);
        public int IndexOf(SimpleRange range) => _ranges.IndexOf(range, true);

        public void Clear()
        {
            _ranges.Clear();
        }

        public List<SimpleRange>.Enumerator GetEnumerator() => _ranges.GetEnumerator();

        IEnumerator<SimpleRange> IEnumerable<SimpleRange>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
