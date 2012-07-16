using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using System.Configuration;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Drawing;

namespace testGstSharp
{
	public class GameWindow1:GameWindow
	{
		//internal ThreadedVideoPlayer videoPlayer;
		internal ThreadedGLSLVideoPlayer videoPlayer;
		
		public GameWindow1():base(1920,1080)
		{
		}

		protected override void OnLoad(EventArgs e)
		{
			base.OnLoad(e);
			Keyboard.KeyUp += new EventHandler<OpenTK.Input.KeyboardKeyEventArgs>(Keyboard_KeyUp);
		 	Closed += new EventHandler<EventArgs>(GameWindow1_Closed);

			this.VSync = VSyncMode.On;
		
			// set up video player, test video: http://www.bigbuckbunny.org
			ThreadedVideoPlayer.Init();
			//videoPlayer = new ThreadedVideoPlayer();
			videoPlayer = new ThreadedGLSLVideoPlayer();
			videoPlayer.LoadVideo(System.Environment.CurrentDirectory + "/test.avi");
		}

		void Keyboard_KeyUp(object sender, OpenTK.Input.KeyboardKeyEventArgs e)
		{
			if (e.Key == OpenTK.Input.Key.Escape){
				this.Exit();
			}
		}

		void GameWindow1_Closed(object sender, EventArgs e)
		{
			this.Exit();
		}
		
		public override void Exit()
		{
			videoPlayer.Stop();
			base.Exit();
		}

		private void SetupViewport()
		{
			int w = Width;
			int h = Height;
			
			GL.Viewport(0, 0, w, h);			
			
			GL.MatrixMode(MatrixMode.Projection);
			GL.LoadIdentity();
			//GL.Ortho(0, w, h, 0, -(w + h) / 2, (w + h) / 2);
			// GL.Ortho(-1.0,1.0,-1.0,1.0,0.0,4.0);
			GL.Ortho(0,1.0,0.0,1.0, 0.0, 1.0);
		}

		protected override void OnUnload(EventArgs e)
		{
			base.OnUnload(e);
		}
		
		protected override void OnResize(EventArgs e)
		{
			base.OnResize(e);
			SetupViewport();
		}


		protected override void OnUpdateFrame(FrameEventArgs e)
		{
			base.OnUpdateFrame(e);
			videoPlayer.Update();
		}

		protected override void OnRenderFrame(FrameEventArgs e)
		{
			base.OnRenderFrame(e);
			
			GL.ClearColor(System.Drawing.Color.Black);
			
			//clear
			GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
			
			//draw video player
			videoPlayer.Draw(0,0,1.0f, 1.0f);
			
			this.SwapBuffers();
		}
		
		[STAThread]
		static void Main(string[] args)
		{
			GameWindow1 gameWindow = new GameWindow1();
			gameWindow.Run(0.0, 0.0);
		}
    }
}
