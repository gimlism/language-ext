using System;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using LanguageExt.Common;
using System.Threading.Tasks;
using static LanguageExt.Prelude;
using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;
using System.Threading;
using LanguageExt.Effects.Traits;
using LanguageExt.Pipes;
using LanguageExt.Thunks;

namespace LanguageExt
{
    /// <summary>
    /// Asynchronous effect monad
    /// </summary>
    public readonly struct Aff<RT, A> 
        where RT : struct, HasCancel<RT>
    {
        internal ThunkAsync<RT, A> Thunk => thunk ?? ThunkAsync<RT, A>.Fail(Errors.Bottom);
        readonly ThunkAsync<RT, A> thunk;

        /// <summary>
        /// Constructor
        /// </summary>
        [MethodImpl(Opt.Default)]
        internal Aff(ThunkAsync<RT, A> thunk) =>
            this.thunk = thunk ?? throw new ArgumentNullException(nameof(thunk));

        /// <summary>
        /// Invoke the effect
        /// </summary>
        [Pure, MethodImpl(Opt.Default)]
        public ValueTask<Fin<A>> Run(in RT env) =>
            Thunk.Value(env);

        /// <summary>
        /// Invoke the effect
        /// </summary>
        [Pure, MethodImpl(Opt.Default)]
        public ValueTask<Fin<A>> ReRun(RT env) =>
            Thunk.ReValue(env);

        /// <summary>
        /// Clone the effect
        /// </summary>
        /// <remarks>
        /// If the effect had already run, then this state will be wiped in the clone, meaning it can be re-run
        /// </remarks>
        [Pure, MethodImpl(Opt.Default)]
        public Aff<RT, A> Clone() =>
            new Aff<RT, A>(Thunk.Clone());        

        /// <summary>
        /// Invoke the effect
        /// </summary>
        /// <remarks>
        /// Throws on error
        /// </remarks>
        [MethodImpl(Opt.Default)]
        public async ValueTask<Unit> RunUnit(RT env) =>
            (await Thunk.Value(env).ConfigureAwait(false)).Case switch
            {
                A _     => unit,
                Error e => e.Throw(),
                _       => throw new NotSupportedException()
            };

        /// <summary>
        /// Launch the async computation without awaiting the result
        /// </summary>
        /// <remarks>
        /// If the parent expression has `cancel` called on it, then it will also cancel the forked child
        /// expression.
        ///
        /// `Fork` returns an `Eff<Unit>` as its bound result value.  If you run it, it will cancel the
        /// forked child expression.
        /// </remarks>
        /// <returns>Returns an `Eff<Unit>` as its bound value.  If it runs, it will cancel the
        /// forked child expression</returns>
        [MethodImpl(Opt.Default)]
        public Eff<RT, Eff<Unit>> Fork()
        {
            var t = Thunk;
            return Eff<RT, Eff<Unit>>(
                env =>
                {
                    // Create a new local runtime with its own cancellation token
                    var lenv = env.LocalCancel;
                    
                    // If the parent cancels, we should too
                    var reg = env.CancellationToken.Register(() => lenv.CancellationTokenSource.Cancel());
                    
                    // Run
                    ignore(t.Value(lenv).Iter(_ => Dispose()));
                    
                    // Return an effect that cancels the fire-and-forget expression
                    return Eff<Unit>(() =>
                                     {
                                         lenv.CancellationTokenSource.Cancel();
                                         Dispose();
                                         return unit;
                                     });

                    void Dispose()
                    {
                        try
                        {
                            reg.Dispose();
                        }
                        catch
                        {
                        }
                    }
                });
        }

        /// <summary>
        /// Lift an asynchronous effect into the Aff monad
        /// </summary>
        [Pure, MethodImpl(Opt.Default)]
        public static Aff<RT, A> Effect(Func<RT, ValueTask<A>> f) =>
            new Aff<RT, A>(ThunkAsync<RT, A>.Lazy(f));

        /// <summary>
        /// Lift an asynchronous effect into the Aff monad
        /// </summary>
        [Pure, MethodImpl(Opt.Default)]
        public static Aff<RT, A> EffectMaybe(Func<RT, ValueTask<Fin<A>>> f) =>
            new Aff<RT, A>(ThunkAsync<RT, A>.Lazy(f));

        /// <summary>
        /// Lift an asynchronous effect into the Aff monad
        /// </summary>
        [Pure, MethodImpl(Opt.Default)]
        public static Aff<RT, Unit> Effect(Func<RT, ValueTask> f) =>
            new Aff<RT, Unit>(ThunkAsync<RT, Unit>.Lazy(async e =>
            {
                await f(e).ConfigureAwait(false);
                return Fin<Unit>.Succ(default);
            }));

        /// <summary>
        /// Lift a value into the Aff monad 
        /// </summary>
        [Pure, MethodImpl(Opt.Default)]
        public static Aff<RT, A> Success(A value) =>
            new Aff<RT, A>(ThunkAsync<RT, A>.Success(value));

        /// <summary>
        /// Lift a failure into the Aff monad 
        /// </summary>
        [Pure, MethodImpl(Opt.Default)]
        public static Aff<RT, A> Fail(Error error) =>
            new Aff<RT, A>(ThunkAsync<RT, A>.Fail(error));

        /// <summary>
        /// Force the operation to end after a time out delay
        /// </summary>
        /// <param name="timeoutDelay">Delay for the time out</param>
        /// <returns>Either success if the operation completed before the timeout, or Errors.TimedOut</returns>
        [Pure, MethodImpl(Opt.Default)]
        public Aff<RT, A> Timeout(TimeSpan timeoutDelay)
        {
            var t = Thunk;
            return AffMaybe<RT, A>(
                async env =>
                {
                    using var delayTokSrc = new CancellationTokenSource();
                    var lenv       = env.LocalCancel;
                    var delay      = Task.Delay(timeoutDelay, delayTokSrc.Token);
                    var task       = t.Value(lenv).AsTask();
                    var completed  = await Task.WhenAny(new Task[] {delay, task}).ConfigureAwait(false);

                    if (completed == delay)
                    {
                        lenv.CancellationTokenSource.Cancel();
                        return FinFail<A>(Errors.TimedOut);
                    }
                    else
                    {
                        delayTokSrc.Cancel();
                        return await task;
                    }
                });
        }

        [Pure, MethodImpl(Opt.Default)]
        public static Aff<RT, A> operator |(Aff<RT, A> ma, Aff<RT, A> mb) =>
            new Aff<RT, A>(ThunkAsync<RT, A>.Lazy(
                                async env =>
                                {
                                    var ra = await ma.ReRun(env).ConfigureAwait(false);
                                    return ra.IsSucc
                                               ? ra
                                               : await mb.ReRun(env).ConfigureAwait(false);
                                }));

        [Pure, MethodImpl(Opt.Default)]
        public static Aff<RT, A> operator |(Aff<RT, A> ma, Aff<A> mb) =>
            new Aff<RT, A>(ThunkAsync<RT, A>.Lazy(
                                async env =>
                                {
                                    var ra = await ma.ReRun(env).ConfigureAwait(false);
                                    return ra.IsSucc
                                               ? ra
                                               : await mb.ReRun().ConfigureAwait(false);
                                }));

        [Pure, MethodImpl(Opt.Default)]
        public static Aff<RT, A> operator |(Aff<A> ma, Aff<RT, A> mb) =>
            new Aff<RT, A>(ThunkAsync<RT, A>.Lazy(
                                async env =>
                                {
                                    var ra = await ma.ReRun().ConfigureAwait(false);
                                    return ra.IsSucc
                                               ? ra
                                               : await mb.ReRun(env).ConfigureAwait(false);
                                }));

        [Pure, MethodImpl(Opt.Default)]
        public static Aff<RT, A> operator |(Aff<RT, A> ma, Eff<RT, A> mb) =>
            new Aff<RT, A>(ThunkAsync<RT, A>.Lazy(
                                async env =>
                                {
                                    var ra = await ma.ReRun(env).ConfigureAwait(false);
                                    return ra.IsSucc
                                               ? ra
                                               : mb.ReRun(env);
                                }));

        [Pure, MethodImpl(Opt.Default)]
        public static Aff<RT, A> operator |(Eff<RT, A> ma, Aff<RT, A> mb) =>
            new Aff<RT, A>(ThunkAsync<RT, A>.Lazy(
                                async env =>
                                {
                                    var ra = ma.ReRun(env);
                                    return ra.IsSucc
                                               ? ra
                                               : await mb.ReRun(env).ConfigureAwait(false);
                                }));

        [Pure, MethodImpl(Opt.Default)]
        public static Aff<RT, A> operator |(Eff<A> ma, Aff<RT, A> mb) =>
            new Aff<RT, A>(ThunkAsync<RT, A>.Lazy(
                                async env =>
                                {
                                    var ra = ma.ReRun();
                                    return ra.IsSucc
                                               ? ra
                                               : await mb.ReRun(env).ConfigureAwait(false);
                                }));

        [Pure, MethodImpl(Opt.Default)]
        public static Aff<RT, A> operator |(Aff<RT, A> ma, Eff<A> mb) =>
            new Aff<RT, A>(ThunkAsync<RT, A>.Lazy(
                                async env =>
                                {
                                    var ra = await ma.ReRun(env).ConfigureAwait(false);
                                    return ra.IsSucc
                                               ? ra
                                               : mb.ReRun();
                                }));

        [Pure, MethodImpl(Opt.Default)]
        public static Aff<RT, A> operator |(Aff<RT, A> ma, EffCatch<A> mb) =>
            new Aff<RT, A>(ThunkAsync<RT, A>.Lazy(
                                async env =>
                                {
                                    var ra = await ma.ReRun(env).ConfigureAwait(false);
                                    return ra.IsSucc
                                               ? ra
                                               : mb.Run(ra.Error);
                                }));

        [Pure, MethodImpl(Opt.Default)]
        public static Aff<RT, A> operator |(Aff<RT, A> ma, AffCatch<A> mb) =>
            new Aff<RT, A>(ThunkAsync<RT, A>.Lazy(
                                async env =>
                                {
                                    var ra = await ma.ReRun(env).ConfigureAwait(false);
                                    return ra.IsSucc
                                               ? ra
                                               : await mb.Run(ra.Error).ConfigureAwait(false);
                                }));

        [Pure, MethodImpl(Opt.Default)]
        public static Aff<RT, A> operator |(Aff<RT, A> ma, EffCatch<RT, A> mb) =>
            new Aff<RT, A>(ThunkAsync<RT, A>.Lazy(
                                async env =>
                                {
                                    var ra = await ma.ReRun(env).ConfigureAwait(false);
                                    return ra.IsSucc
                                               ? ra
                                               : mb.Run(env, ra.Error);
                                }));

        [Pure, MethodImpl(Opt.Default)]
        public static Aff<RT, A> operator |(Aff<RT, A> ma, AffCatch<RT, A> mb) =>
            new Aff<RT, A>(ThunkAsync<RT, A>.Lazy(
                                async env =>
                                {
                                    var ra = await ma.ReRun(env).ConfigureAwait(false);
                                    return ra.IsSucc
                                               ? ra
                                               : await mb.Run(env, ra.Error).ConfigureAwait(false);
                                }));

        [Pure, MethodImpl(Opt.Default)]
        public static Aff<RT, A> operator |(Aff<RT, A> ma, CatchValue<A> value) =>
            new Aff<RT, A>(ThunkAsync<RT, A>.Lazy(
                                async env =>
                                {
                                    var ra = await ma.ReRun(env).ConfigureAwait(false);
                                    return ra.IsSucc
                                               ? ra
                                               : value.Match(ra.Error)
                                                   ? FinSucc(value.Value(ra.Error))
                                                   : ra;
                                }));

        [Pure, MethodImpl(Opt.Default)]
        public static Aff<RT, A> operator |(Aff<RT, A> ma, CatchError value) =>
            new Aff<RT, A>(ThunkAsync<RT, A>.Lazy(
                                async env =>
                                {
                                    var ra = await ma.ReRun(env).ConfigureAwait(false);
                                    return ra.IsSucc
                                               ? ra
                                               : value.Match(ra.Error)
                                                   ? FinFail<A>(value.Value(ra.Error))
                                                   : ra;
                                }));

        /// <summary>
        /// Implicit conversion from pure Aff
        /// </summary>
        public static implicit operator Aff<RT, A>(Aff<A> ma) =>
            EffectMaybe(env => ma.ReRun());

        /// <summary>
        /// Implicit conversion from pure Eff
        /// </summary>
        public static implicit operator Aff<RT, A>(Eff<A> ma) =>
            EffectMaybe(env => ma.ReRun().AsValueTask());

        /// <summary>
        /// Implicit conversion from Eff
        /// </summary>
        public static implicit operator Aff<RT, A>(Eff<RT, A> ma) =>
            EffectMaybe(env => ma.ReRun(env).AsValueTask());
    }
}
