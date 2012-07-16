using System;
using Gst;
using Gst.App;
using Gst.BasePlugins;
using Gst.Interfaces;
using OpenTK.Graphics.OpenGL;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading;

/*
BUG:
http://lists.freedesktop.org/archives/gstreamer-bugs/2010-August/068387.html

Fix:
Download latest source and compile.
Replace /usr/lib/cli/gstreamer-sharp-0.9/gstreamer-sharp.dll.

*/

namespace testGstSharp
{
	public class ThreadedVideoPlayer
	{

        public enum VideoPlayerState{
            STOPPED,
            LOADING,
            PLAYING,
            PAUSED,
        }   

		static bool _gstInited = false;
		// Gst depends on GLib loop for event handling
		public static void Init(){
			if(!_gstInited){
				Gst.Application.Init();
			}
		}
		
		//Using Playbin2 and AppSink
		PlayBin2 playBin = null;
		AppSink appSink = null;
		int width = 0;
		int height = 0;
		byte[] buffer = null;
        internal int textureID = 0;
        int[] pboIDs = new int[2];
        int pboIndex = 0;
        bool texturesOK = false;
		
		bool isFrameNew = false;
		object lockFrameBuf = new object();

		bool isrunning = true;
		Thread gstThread;


        VideoPlayerState playerState = VideoPlayerState.STOPPED;
        internal VideoPlayerState PlayerState { get { return playerState; } private set { playerState = value; } }


        //Linux GstreamerSharp bug, see https://bugzilla.gnome.org/show_bug.cgi?id=636804
		
        [DllImport("libgstreamer-0.10.so", CallingConvention = CallingConvention.Cdecl)]
        static extern void gst_mini_object_unref(IntPtr raw);

		
		public ThreadedVideoPlayer ()
		{
			//do nothing
		}
		
		public void LoadVideo(string uri){

            if (gstThread != null)
            {
                isrunning = false;
                gstThread.Join();
                gstThread = new Thread(new ThreadStart(KeepPolling));
            }

			if(playBin != null){
                playerState = VideoPlayerState.STOPPED;
                Console.WriteLine("STOPPED");

				//Dispose playbin2 and appsink
                playBin.SetState(State.Null);
                playBin.Dispose();
                appSink.SetState(State.Null);
                appSink.Dispose();
				
				//Create playbin2 and appsink
                playBin = new PlayBin2();

                appSink = ElementFactory.Make("appsink", "sink") as AppSink;
                //appSink.Caps = new Caps("video/x-raw-yuv", new object[]{});
                appSink.Caps = new Caps("video/x-raw-rgb", new object[] { "bpp", 24 });
                appSink.Drop = true;
                appSink.MaxBuffers = 1;
                playBin.VideoSink = appSink;
			}else{
				//Create playbin2 and appsink
				playBin = new PlayBin2();
                appSink = ElementFactory.Make("appsink", "sink") as AppSink;
                //appSink.Caps = new Caps("video/x-raw-yuv", new object[]{});
                appSink.Caps = new Caps("video/x-raw-rgb", new object[] { "bpp", 24 });
                appSink.Drop = true;
                appSink.MaxBuffers = 1;
                playBin.VideoSink = appSink;
			}

			//init variables
            texturesOK = false;
            width = 0;
            height = 0;
			
			//Set file uri
            string validUri = uri;
            if (!validUri.StartsWith("file://"))
            {
                validUri = "file://" + uri;
            }
            playBin.Uri = validUri;
			StateChangeReturn sr = playBin.SetState(State.Playing);
			Console.WriteLine(sr.ToString());
            playerState = VideoPlayerState.LOADING;
            Console.WriteLine("LOADING:" + validUri);

            if (gstThread == null)
            {
                gstThread = new Thread(new ThreadStart(KeepPolling));
            }

            isrunning = true;
			//Start polling thread...future thought, using async queue?
            gstThread.Start();

            return;
		}
		
		void KeepPolling(){
			
			while(isrunning){

                switch (playerState)
                {
                    case VideoPlayerState.STOPPED:
                        Thread.Sleep(10);
                        break;
                    case VideoPlayerState.LOADING:
                        //get video width/height
                        int w = 0, h = 0;
					
						//Query video information
                        Gst.Buffer buf = appSink.PullBuffer();
					    if(buf != null){

                            Console.WriteLine(buf.Caps.ToString());
						
							//string format = buf.Caps[0].GetValue("format").Val.ToString();
							//Console.WriteLine("format: " + format);
                            int.TryParse(buf.Caps[0].GetValue("width").Val.ToString(), out w);
                            int.TryParse(buf.Caps[0].GetValue("height").Val.ToString(), out h);
                            if (w * h != 0)
                            {
								//Create decoded buffer
                                lock (lockFrameBuf)
                                {
                                    width = w;
                                    height = h;
                                    buffer = new byte[width * height * 3];
                                    Marshal.Copy(buf.Data, buffer, 0, width * height * 3);
                                    isFrameNew = true;
									//Dispose handle to avoid memory leak
                                    //gst_mini_object_unref(buf.Handle);
                                    buf.Dispose();
                                }
                              
                                Console.WriteLine("PLAYING");
                                playerState = VideoPlayerState.PLAYING;
								
							
								continue;
                            }
						}
						//Nothing
                        Thread.Sleep(10);
                        break;
                    case VideoPlayerState.PLAYING:
                        Gst.Buffer buf2 = appSink.PullBuffer();
					    if(buf2 != null){
						    lock(lockFrameBuf){
								//Update buffer
							    Marshal.Copy(buf2.Data, buffer, 0, width * height * 3);
							    isFrameNew = true;
                                //gst_mini_object_unref(buf2.Handle);
                                //buf2.Dispose();
						    }
						
						    buf2.Dispose();
					    }else{
                            lock (lockFrameBuf)
                            {
                                //Clear buffer
                                buffer = new byte[width * height * 3];
                            }
                            playerState = VideoPlayerState.STOPPED;
                            Console.WriteLine("STOPPED");

					    }
                        break;
                    case VideoPlayerState.PAUSED:
						//Do nothing
                        Thread.Sleep(10);
                        break;
                    default:
						//Do nothing
                        Thread.Sleep(10);
                        break;
                }
			}
			
			//Clean up
            this.PlayerState = VideoPlayerState.STOPPED;
            playBin.SetState(State.Null);
            playBin.Dispose();
            appSink.SetState(State.Null);
            appSink.Dispose();
            playBin = null;
            appSink = null;
		}
	
		public void Stop(){
			isrunning = false;
            if (gstThread != null)
                gstThread.Join();
		}
		
		public void Update(){

            switch (playerState)
            {
                case VideoPlayerState.LOADING:
                    break;
                case VideoPlayerState.PAUSED:
                    break;
                case VideoPlayerState.PLAYING:
                    if (!texturesOK)
                    {
                        SetupTexture(width, height);
                        SetupPBOs(width, height);
                        texturesOK = true;
                        lock (lockFrameBuf)
                        {
							//New frame arrived, update PBO
                            UpdatePBO(buffer, width, height);
                        }
                    }
                    if (isFrameNew)
                    {
                        lock (lockFrameBuf)
                        {
							//New frame arrived, update PBO
                            UpdatePBO(buffer, width, height);
                        }
                        isFrameNew = false;
                    }
                    break;
                case VideoPlayerState.STOPPED:
                    break;
            }
		}
		
		public void Draw(int x, int y, int w, int h){
			
			
            if (textureID == 0)
            {
                //Clear buffer when nothing loaded
                buffer = new byte[w * h * 3];
                SetupTexture(w, h);
            }
			
			//Draw quad texture
			GL.Enable(EnableCap.Texture2D);
	        GL.BindTexture(TextureTarget.Texture2D, textureID);
	        GL.Begin(BeginMode.Quads);
	
	        double left = x;
	        double top = y;
	        double width = w;
	        double height = h;
	
	        GL.TexCoord2(0f, 0f); GL.Vertex2(left, top);
	        GL.TexCoord2(0f, 1f); GL.Vertex2(left, top + height);
	        GL.TexCoord2(1f, 1f); GL.Vertex2(left + width, top + height);
	        GL.TexCoord2(1f, 0f); GL.Vertex2(left + width, top);
	
	        GL.Disable(EnableCap.Texture2D);
	        GL.End();
			
		}
		
		// Create PBOs
        void SetupPBOs(int w, int h)
        {
            if (pboIDs[0] == 0) GL.GenBuffers(1, out pboIDs[0]);
            if (pboIDs[1] == 0) GL.GenBuffers(1, out pboIDs[1]);
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, pboIDs[0]);
            GL.BufferData(BufferTarget.PixelUnpackBuffer, new IntPtr(w * h * 3), IntPtr.Zero, BufferUsageHint.StreamDraw);

            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, pboIDs[1]);
            GL.BufferData(BufferTarget.PixelUnpackBuffer, new IntPtr(w * h * 3), IntPtr.Zero, BufferUsageHint.StreamDraw);
        }
		
		// Setup texture
		void SetupTexture(int w, int h){
			
			GL.Enable(EnableCap.Texture2D);
            if(textureID == 0)
    			textureID = GL.GenTexture();
            
		    GL.BindTexture(TextureTarget.Texture2D, textureID);

		    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb, w, h, 0, PixelFormat.Rgb, PixelType.UnsignedByte, buffer);

		    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
		    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);
            
		    GL.Disable(EnableCap.Texture2D);
		}
		
		// Ref: http://www.songho.ca/opengl/gl_pbo.html
        void UpdatePBO(byte[] buf, int w, int h)
        {
            // increment current index first then get the next index
            // "index" is used to copy pixels from a PBO to a texture object
            // "nextIndex" is used to update pixels in a PBO
            pboIndex = (pboIndex + 1) % 2;
            int nextIndex = (pboIndex + 1) % 2;


            // bind the texture and PBO
            GL.BindTexture(TextureTarget.Texture2D, textureID);
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, pboIDs[pboIndex]);

            // copy pixels from PBO to texture object
            // Use offset instead of ponter.
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, w, h, PixelFormat.Rgb, PixelType.UnsignedByte, IntPtr.Zero);

            // bind PBO to update pixel values
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, pboIDs[nextIndex]);

            // map the buffer object into client's memory
            // Note that glMapBufferARB() causes sync issue.
            // If GPU is working with this buffer, glMapBufferARB() will wait(stall)
            // for GPU to finish its job. To avoid waiting (stall), you can call
            // first glBufferDataARB() with NULL pointer before glMapBufferARB().
            // If you do that, the previous data in PBO will be discarded and
            // glMapBufferARB() returns a new allocated pointer immediately
            // even if GPU is still working with the previous data.
            GL.BufferData(BufferTarget.PixelUnpackBuffer, new IntPtr(w * h * 3), IntPtr.Zero, BufferUsageHint.StreamDraw);
            IntPtr dest = GL.MapBuffer(BufferTarget.PixelUnpackBuffer, BufferAccess.WriteOnly);
            if (dest != IntPtr.Zero)
            {
                // update data directly on the mapped buffer
                //CopyMemory(dest, buf, (uint)(w * h * 3));
                Marshal.Copy(buf, 0, dest, w * h * 3);
                GL.UnmapBuffer(BufferTarget.PixelUnpackBuffer); // release pointer to mapping buffer
            }

            // it is good idea to release PBOs with ID 0 after use.
            // Once bound with 0, all pixel operations behave normal ways.
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0);
        }
	}
}

