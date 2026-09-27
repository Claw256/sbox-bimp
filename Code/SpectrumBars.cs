using System;
using Sandbox.UI;

namespace Bimp;

/// <summary>
/// A row of bars that bounce to the audio of a <see cref="MediaPlayer"/>.
/// Uses the real spectrum (in dB, auto-ranged) when playing audio only, otherwise a gentle fake animation
/// (the engine doesn't expose the spectrum of video playback).
/// </summary>
public sealed class SpectrumBars : Panel
{
	public MediaPlayer Player { get; set; }

	public int BarCount { get; }

	readonly Panel[] bars;
	readonly float[] smoothed;
	float[] levels;

	/// <summary>
	/// The engine's spectrum is linear FFT magnitudes: 0.001 to ~66, median ~0.2, the bass tens of dB above the treble
	/// (measured). Bars show each band in dB within this window below the loudest band.
	/// </summary>
	const float RangeDb = 30;

	/// <summary> Treble has far less energy than bass - lift it so the right side of the display moves too. </summary>
	const float TiltDbPerOctave = 3;

	/// <summary> The top of the window follows the loudest band at once and falls back this fast, so quiet and loud tracks (and any volume) fill the bars. </summary>
	const float CeilingFallDbPerSecond = 6;

	/// <summary> The lowest the window's top goes, so silence and near silence stay at the bottom. </summary>
	const float MinCeilingDb = -6;

	float ceilingDb = MinCeilingDb;

	/// <summary> Each bar's shown height, 0-1 (for probes). </summary>
	internal IReadOnlyList<float> Shown => smoothed;

	public SpectrumBars() : this( 32 ) { }

	public SpectrumBars( int count )
	{
		BarCount = count;
		AddClass( "spectrumbars" );

		bars = new Panel[count];
		smoothed = new float[count];

		for ( int i = 0; i < count; i++ )
			bars[i] = Add.Panel( "bar" );
	}

	public override void Tick()
	{
		base.Tick();

		var backend = Player?.Backend;
		var spectrum = backend is not null ? backend.Spectrum : ReadOnlySpan<float>.Empty;
		var amp = backend?.Amplitude ?? 0;
		var playing = backend is not null && backend.Loaded && !(Player?.Paused ?? true);

		var real = playing && spectrum.Length > 2;
		if ( real ) Levels( spectrum );

		for ( int i = 0; i < BarCount; i++ )
		{
			float v = 0;

			if ( real )
			{
				v = Math.Clamp( (levels[i] - (ceilingDb - RangeDb)) / RangeDb, 0, 1 );
			}
			else if ( playing )
			{
				v = 0.35f + 0.25f * MathF.Sin( RealTime.Now * 3.1f + i * 0.7f ) * MathF.Sin( RealTime.Now * 1.7f + i * 0.31f ) + amp;
			}

			// fast attack, slow release
			smoothed[i] = v > smoothed[i] ? v : MathX.Lerp( smoothed[i], v, RealTime.Delta * 8.0f );
			bars[i].Style.Height = Length.Percent( (smoothed[i] * 100.0f).Clamp( 3, 100 ) );
		}
	}

	/// <summary>
	/// Each bar's band level in dB (tilted, see <see cref="TiltDbPerOctave"/>) into <see cref="levels"/>, and the window's
	/// top moved to the loudest. Bands widen towards the treble (squared), skipping the DC bin and the top third (above
	/// ~16 kHz at 48 kHz, where there's next to nothing).
	/// </summary>
	void Levels( ReadOnlySpan<float> spectrum )
	{
		levels ??= new float[BarCount];
		var usable = Math.Max( BarCount, spectrum.Length * 2 / 3 );
		var peak = float.MinValue;

		for ( int i = 0; i < BarCount; i++ )
		{
			// at least one bin of its own each, so the bass bars don't all show the same one
			int Edge( int k ) => 1 + k + (int)(MathF.Pow( k / (float)BarCount, 2 ) * (usable - BarCount));
			var start = Edge( i );
			var end = Edge( i + 1 );

			float m = 0;
			for ( int s = start; s < end && s < spectrum.Length; s++ )
				m = MathF.Max( m, spectrum[s] );

			var center = (start + end) * 0.5f;
			levels[i] = 20 * MathF.Log10( m + 1e-5f ) + TiltDbPerOctave * MathF.Log2( center );
			peak = MathF.Max( peak, levels[i] );
		}

		ceilingDb = peak > ceilingDb ? peak : MathF.Max( MinCeilingDb, ceilingDb - CeilingFallDbPerSecond * RealTime.Delta );
	}
}
