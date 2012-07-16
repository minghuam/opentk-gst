using System;
using Gst;
using Gst.App;
using Gst.BasePlugins;
using Gst.Interfaces;
using OpenTK.Graphics.OpenGL;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading;
using System.IO;

/*
BUG:
http://lists.freedesktop.org/archives/gstreamer-bugs/2010-August/068387.html

Fix:
Download latest source and compile.
Replace /usr/lib/cli/gstreamer-sharp-0.9/gstreamer-sharp.dll.

Support only YUV420 planar!!!

*/

namespace testGstSharp
{
	public class ThreadedGLSLVideoPlayer
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
		byte[] bufferY = null;
		byte[] bufferU = null;
		byte[] bufferV = null;
        int[] textureIDs = new int[3];
        bool texturesOK = false;
		
		bool isFrameNew = false;
		object lockFrameBuf = new object();

		bool isrunning = true;
		Thread gstThread;

		int vertex_shader_object = 0;
		int fragment_shader_object = 0;
		int shader_program = 0;
		
		int[] m_shader_sampler = new int[3];
		
        VideoPlayerState playerState = VideoPlayerState.STOPPED;
        internal VideoPlayerState PlayerState { get { return playerState; } private set { playerState = value; } }


        //Linux GstreamerSharp bug, see https://bugzilla.gnome.org/show_bug.cgi?id=636804
		
        [DllImport("libgstreamer-0.10.so", CallingConvention = CallingConvention.Cdecl)]
        static extern void gst_mini_object_unref(IntPtr raw);
		
		void CreateShaders(string vs, string fs, out int vertexObject, out int fragmentObject, out int program)
        {
            int status_code;
            string info;

            vertexObject = GL.CreateShader(ShaderType.VertexShader);
            fragmentObject = GL.CreateShader(ShaderType.FragmentShader);

            // Compile vertex shader
            GL.ShaderSource(vertexObject, vs);
            GL.CompileShader(vertexObject);
            GL.GetShaderInfoLog(vertexObject, out info);
            GL.GetShader(vertexObject, ShaderParameter.CompileStatus, out status_code);

            if (status_code != 1)
                throw new ApplicationException(info);

            // Compile vertex shader
            GL.ShaderSource(fragmentObject, fs);
            GL.CompileShader(fragmentObject);
            GL.GetShaderInfoLog(fragmentObject, out info);
            GL.GetShader(fragmentObject, ShaderParameter.CompileStatus, out status_code);
            
            if (status_code != 1)
                throw new ApplicationException(info);

            program = GL.CreateProgram();
            GL.AttachShader(program, fragmentObject);
            GL.AttachShader(program, vertexObject);

            GL.LinkProgram(program);
            GL.UseProgram(program);
        }
		
		public ThreadedGLSLVideoPlayer()
		{
			//check glsl
			string version = GL.GetString(StringName.Version);
			int major = (int)version[0];
			if(major < 2){
				Console.WriteLine("OpenGL 2.0 not available. GLSL not supported.");
			}
			
			//load glsl
			//Ref: https://github.com/x-quadraht/justcutit
			using (StreamReader vs = new StreamReader("yuvtorgb_vertex.glsl"))
            using (StreamReader fs = new StreamReader("yuvtorgb_fragment.glsl"))
                CreateShaders(vs.ReadToEnd(), fs.ReadToEnd(),
                    out vertex_shader_object, out fragment_shader_object,
                    out shader_program);
			
			m_shader_sampler[0] = GL.GetUniformLocation(shader_program, "y_sampler");
			m_shader_sampler[1] = GL.GetUniformLocation(shader_program, "u_sampler");
			m_shader_sampler[2] = GL.GetUniformLocation(shader_program, "v_sampler");
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
                appSink.Caps = new Caps("video/x-raw-yuv", new object[]{});
                //appSink.Caps = new Caps("video/x-raw-rgb", new object[] { "bpp", 24 });
                appSink.Drop = true;
                appSink.MaxBuffers = 8;
                playBin.VideoSink = appSink;
			}else{
				//Create playbin2 and appsink
				playBin = new PlayBin2();
                appSink = ElementFactory.Make("appsink", "sink") as AppSink;
                appSink.Caps = new Caps("video/x-raw-yuv", new object[]{});
                //appSink.Caps = new Caps("video/x-raw-rgb", new object[] { "bpp", 24 });
                appSink.Drop = true;
                appSink.MaxBuffers = 8;
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
		
		// GST polling thread function
		void KeepPolling(){
			while(isrunning){
				switch (playerState)
                { 
                    case VideoPlayerState.STOPPED:
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
	                                bufferY = new byte[width * height];
	                                bufferU = new byte[width * height/4];
	                                bufferV = new byte[width * height/4];
									IntPtr src = buf.Data;
									Marshal.Copy(src, bufferY, 0, width * height);
									src = new IntPtr(src.ToInt64() + width*height);
									Marshal.Copy(src, bufferU, 0, width * height/4);
									src = new IntPtr(src.ToInt64() + width*height/4);
									Marshal.Copy(src, bufferV, 0, width * height/4);
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
                        break;
                    case VideoPlayerState.PLAYING:
                        Gst.Buffer buf2 = appSink.PullBuffer();
					    if(buf2 != null){
						    lock(lockFrameBuf){
								//Update buffer
								IntPtr src = buf2.Data;
								Marshal.Copy(src, bufferY, 0, width * height);
								src = new IntPtr(src.ToInt64() + width*height);
								Marshal.Copy(src, bufferU, 0, width * height/4);
								src = new IntPtr(src.ToInt64() + width*height/4);
								Marshal.Copy(src, bufferV, 0, width * height/4);
								isFrameNew = true;
                                //gst_mini_object_unref(buf2.Handle);
                                //buf2.Dispose();
						    }
						    buf2.Dispose();
					    }else{
                            lock (lockFrameBuf)
                            {
                                //Clear buffer
                                bufferY = new byte[width * height];
                                bufferU = new byte[width * height/4];
                                bufferV = new byte[width * height/4];
                            }
                            playerState = VideoPlayerState.STOPPED;
                            Console.WriteLine("STOPPED");
					    }
                    
                        break;
                    case VideoPlayerState.PAUSED:
						//Do nothing
                        break;
                    default:
						//Do nothing
                        break;
                }
				Thread.Sleep(10);
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
	
		/// <summary>
		/// Stop this instance.
		/// </summary>
		public void Stop(){
			isrunning = false;
            if (gstThread != null)
                gstThread.Join();
			
			if (shader_program != 0)
                GL.DeleteProgram(shader_program);
            if (fragment_shader_object != 0)
                GL.DeleteShader(fragment_shader_object);
            if (vertex_shader_object != 0)
                GL.DeleteShader(vertex_shader_object);
		}
		
		/// <summary>
		/// Update this instance.
		/// </summary>
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
                        texturesOK = true;
                    }
                    if (isFrameNew)
                    {
                        lock (lockFrameBuf)
                        {
							//New frame arrived, update tex
                            UpdateTexture(width, height);
                        }
                        isFrameNew = false;
                    }
                    break;
                case VideoPlayerState.STOPPED:
                    break;
            }
		}
		
		/// <summary>
		/// Draw the specified x, y, w and h.
		/// </summary>
		public void Draw(float x, float y, float w, float h){
			
			if(!texturesOK) return;
			
			float[] points = {
							x, y,
							x+w, y,
							x+w, y+h,
							x, y+h
			};
			
			float[] texcoords = {
							0.0f, 1.0f,
							1.0f, 1.0f,
							1.0f, 0.0f,
							0.0f, 0.0f
			};
			
			//Draw quad texture
			GL.Enable(EnableCap.Texture2D);
	       
			GL.EnableClientState(ArrayCap.VertexArray);
			GL.VertexPointer(2, VertexPointerType.Float, 0, points);
			
			//Y
			GL.ClientActiveTexture(TextureUnit.Texture0);
			GL.EnableClientState(ArrayCap.TextureCoordArray);
			GL.TexCoordPointer(2, TexCoordPointerType.Float, 0, texcoords);
			//U
			GL.ClientActiveTexture(TextureUnit.Texture1);
			GL.EnableClientState(ArrayCap.TextureCoordArray);
			GL.TexCoordPointer(2, TexCoordPointerType.Float, 0, texcoords);
			//V
			GL.ClientActiveTexture(TextureUnit.Texture2);
			GL.EnableClientState(ArrayCap.TextureCoordArray);
			GL.TexCoordPointer(2, TexCoordPointerType.Float, 0, texcoords);
			
			GL.DrawArrays(BeginMode.Polygon, 0, 4);
			
			GL.DisableClientState(ArrayCap.VertexArray);
			
	        GL.Disable(EnableCap.Texture2D);
		}
		
		/// <summary>
		/// Updates the texture.
		/// </summary>
		/// <param name='w'>
		/// Texture Width.
		/// </param>
		/// <param name='h'>
		/// Texture Height.
		/// </param>
		void UpdateTexture(int w, int h)
        {
			
			GL.Enable(EnableCap.Texture2D);
			//Y
			GL.ActiveTexture(TextureUnit.Texture0);
			GL.BindTexture(TextureTarget.Texture2D, textureIDs[0]);
			//GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, w, h, PixelFormat.Luminance, PixelType.UnsignedByte, bufferY);
			GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.One, w, h, 0, PixelFormat.Luminance, PixelType.UnsignedByte, bufferY);
			
			//U
			GL.ActiveTexture(TextureUnit.Texture1);
			GL.BindTexture(TextureTarget.Texture2D, textureIDs[1]);
			//GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, w/2, h/2, PixelFormat.Luminance, PixelType.UnsignedByte, bufferU);
			GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.One, w/2, h/2, 0, PixelFormat.Luminance, PixelType.UnsignedByte, bufferU);
			
			//V
			GL.ActiveTexture(TextureUnit.Texture2);
			GL.BindTexture(TextureTarget.Texture2D, textureIDs[2]);
			//GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, w/2, h/2, PixelFormat.Luminance, PixelType.UnsignedByte, bufferV);
			GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.One, w/2, h/2, 0, PixelFormat.Luminance, PixelType.UnsignedByte, bufferV);	
		    
			GL.Disable(EnableCap.Texture2D);
		}
		
		/// <summary>
		/// Setups the texture.
		/// </summary>
		/// <param name='w'>
		/// Texture Width.
		/// </param>
		/// <param name='h'>
		/// Texture Height.
		/// </param>
		void SetupTexture(int w, int h){
			
			GL.Enable(EnableCap.Texture2D);
         	for(int i=0; i<3; i++){
				if(textureIDs[i] == 0) textureIDs[i] = GL.GenTexture();
			}
            
			//Y
			GL.ActiveTexture(TextureUnit.Texture0);
		    GL.BindTexture(TextureTarget.Texture2D, textureIDs[0]);
		    //GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.One, w, h, 0, PixelFormat.Luminance, PixelType.UnsignedByte, bufferY);
		    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
		    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.Uniform1(m_shader_sampler[0], 0);
			
			//U
			GL.ActiveTexture(TextureUnit.Texture1);
		    GL.BindTexture(TextureTarget.Texture2D, textureIDs[1]);
		    //GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.One, w/2, h/2, 0, PixelFormat.Luminance, PixelType.UnsignedByte, bufferU);
		    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
		    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.Uniform1(m_shader_sampler[1], 1);
			
			//V
			GL.ActiveTexture(TextureUnit.Texture2);
		    GL.BindTexture(TextureTarget.Texture2D, textureIDs[2]);
		    //GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.One, w/2, h/2, 0, PixelFormat.Luminance, PixelType.UnsignedByte, bufferV);
		    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
		    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
			GL.Uniform1(m_shader_sampler[2], 2);
			
		    GL.Disable(EnableCap.Texture2D);
		}
		
	}
}

