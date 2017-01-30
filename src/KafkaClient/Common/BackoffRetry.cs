﻿using System;

namespace KafkaClient.Common
{
    public class BackoffRetry : Retry
    {
        private readonly TimeSpan? _maxDelay;
        private readonly TimeSpan _delay;
        private readonly bool _isLinear;

        public BackoffRetry(TimeSpan? timeout, TimeSpan delay, int? maxAttempts = null, bool isLinear = false, TimeSpan? maxDelay = null)
            : base (timeout, maxAttempts)
        {
            _delay = delay;
            _isLinear = isLinear;
            _maxDelay = maxDelay;
        }

        protected override TimeSpan? GetDelay(int attempt, TimeSpan timeTaken)
        {
            if (timeTaken > Timeout) return null;

            double delayMilliseconds;
            if (_isLinear) {
                // multiplied backoff
                delayMilliseconds = _delay.TotalMilliseconds*(attempt + 1);
            } else {
                // exponential backoff
                // from: http://alexandrebrisebois.wordpress.com/2013/02/19/calculating-an-exponential-back-off-delay-based-on-failed-attempts/
                delayMilliseconds = _delay.TotalMilliseconds / 2d * (Math.Pow(2d, attempt + 1) - 1d);
            }

            if (_maxDelay.HasValue && delayMilliseconds > _maxDelay.Value.TotalMilliseconds) {
                delayMilliseconds = _maxDelay.Value.TotalMilliseconds;
            }

            var remainingMilliseconds = Timeout?.TotalMilliseconds - timeTaken.TotalMilliseconds ?? double.MaxValue;
            if (delayMilliseconds > remainingMilliseconds) {
                delayMilliseconds = remainingMilliseconds;
            }

            return TimeSpan.FromMilliseconds(delayMilliseconds);
        }
    }
}