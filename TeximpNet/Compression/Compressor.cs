﻿/*
* Copyright (c) 2016-2017 TeximpNet - Nicholas Woodfield
* 
* Permission is hereby granted, free of charge, to any person obtaining a copy
* of this software and associated documentation files (the "Software"), to deal
* in the Software without restriction, including without limitation the rights
* to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
* copies of the Software, and to permit persons to whom the Software is
* furnished to do so, subject to the following conditions:
* 
* The above copyright notice and this permission notice shall be included in
* all copies or substantial portions of the Software.
* 
* THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
* IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
* FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
* AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
* LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
* OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
* THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.IO;
using TeximpNet.Unmanaged;

namespace TeximpNet.Compression
{
    /// <summary>
    /// A compressor processes input image data (either from a <see cref="Surface"/> or just raw image data) using the Nvidia Texture Tools
    /// API and outputs either to a file (e.g. DDS) or to memory. Processing can range from mipmap creation (with a variety of filters) to compressing RGBA
    /// data into a number of GPU compressed formats. Both 2D and Cubemap textures can be processed.
    /// </summary>
    public sealed class Compressor : IDisposable
    {
        private IntPtr m_compressorPtr;
        private IntPtr m_inputOptionsPtr;
        private IntPtr m_compressionOptionsPtr;
        private IntPtr m_outputOptionsPtr;

        private InputOptions m_inputOptions;
        private CompressionOptions m_compressionOptions;
        private OutputOptions m_outputOptions;
        private bool m_isDisposed;

        //For writing to memory, but returning a list of images
        private List<CompressedImageData> m_mipChain;
        private CompressedImageData m_currentMip;
        private CompressionFormat m_format;
        private int m_currentBytePos;

        //For writing to memory, but writing out to a stream (e.g. writing file contents including headers)
        private bool m_outputtingToStream;
        private Stream m_currentStream;
        private byte[] m_tempBuffer;
        private IntPtr m_pinnedTempBufferPtr;

        /// <summary>
        /// Gets the pointer to the native object.
        /// </summary>
        public IntPtr NativePtr
        {
            get
            {
                return m_compressorPtr;
            }
        }

        /// <summary>
        /// Gets if the compressor has been disposed or not.
        /// </summary>
        public bool IsDisposed
        {
            get
            {
                return m_isDisposed;
            }
        }

        /// <summary>
        /// Gets the input options, this allows you to set input image data and a number of options for how to handle it.
        /// </summary>
        public InputOptions Input
        {
            get
            {
                return m_inputOptions;
            }
        }

        /// <summary>
        /// Gets the compression options.
        /// </summary>
        public CompressionOptions Compression
        {
            get
            {
                return m_compressionOptions;
            }
        }

        /// <summary>
        /// Gets the output options, this allows you to set how the output images should be treated.
        /// </summary>
        public OutputOptions Output
        {
            get
            {
                return m_outputOptions;
            }
        }

        /// <summary>
        /// Constructs a new instance of the <see cref="Compressor"/> class.
        /// </summary>
        public Compressor()
        {
            m_compressorPtr = NvTextureToolsLibrary.Instance.CreateCompressor();
            m_inputOptionsPtr = NvTextureToolsLibrary.Instance.CreateInputOptions();
            m_compressionOptionsPtr = NvTextureToolsLibrary.Instance.CreateCompressionOptions();
            m_outputOptionsPtr = NvTextureToolsLibrary.Instance.CreateOutputOptions();

            m_inputOptions = new InputOptions(m_inputOptionsPtr);
            m_compressionOptions = new CompressionOptions(m_compressionOptionsPtr);
            m_outputOptions = new OutputOptions(m_outputOptionsPtr, BeginImage, OutputData, EndImage);

            m_isDisposed = false;
        }

        /// <summary>
        /// Finalizes an instance of the <see cref="Compressor"/> class.
        /// </summary>
        ~Compressor()
        {
            Dispose(false);
        }

        /// <summary>
        /// Executes processing of input image data, and outputs the images in DDS format to a file (optionally without the header).
        /// </summary>
        /// <param name="outputFileName">Output file name, if it exists the file will get overwritten.</param>
        /// <returns>True if the image was successfully processed and saved, false if otherwise.</returns>
        public bool Process(String outputFileName)
        {
            if(String.IsNullOrEmpty(outputFileName) || !m_inputOptions.HasData)
                return false;

            m_outputtingToStream = false;
            m_outputOptions.SetOutputToFile(outputFileName);

            return NvTextureToolsLibrary.Instance.Process(m_compressorPtr, m_inputOptionsPtr, m_compressionOptionsPtr, m_outputOptionsPtr);
        }

        /// <summary>
        /// Executes processing of input image data, and outputs the images in DDS format to the stream (optionally without the header).
        /// </summary>
        /// <param name="stream">Output stream to write the image file to.</param>
        /// <returns>True if the image was successfully processed and outputted to the stream, false if otherwise.</returns>
        public bool Process(Stream stream)
        {
            if (stream == null || !stream.CanWrite)
                return false;

            m_outputtingToStream = true;
            m_outputOptions.SetOutputToMemory(true);

            m_currentStream = stream;

            if (m_tempBuffer == null)
                m_tempBuffer = new byte[4096];

            m_pinnedTempBufferPtr = MemoryHelper.PinObject(m_tempBuffer);

            try
            {
                return NvTextureToolsLibrary.Instance.Process(m_compressorPtr, m_inputOptionsPtr, m_compressionOptionsPtr, m_outputOptionsPtr);
            }
            finally
            {
                //Make sure we don't hold onto any references
                m_currentStream = null;
                m_outputtingToStream = false;

                MemoryHelper.UnpinObject(m_tempBuffer);
                m_pinnedTempBufferPtr = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Executes processing of input image data, and outputs the images to a list of <see cref="CompressedImageData"/> objects.
        /// For 2D textures, the list will be the mipmap chain (largest mip to smallest). In the case of cubemap textures,
        /// the list will have the mipmap chain of each face sequentially (e.g. all mips for Positive_X, then all mips for Negative_X, and so on).
        /// </summary>
        /// <param name="mipChain">List to hold output mip map images.</param>
        /// <returns>True if the image was successfully processed and outputted to the list, false if otherwise.</returns>
        public bool Process(List<CompressedImageData> mipChain)
        {
            if(mipChain == null || !m_inputOptions.HasData)
                return false;

            m_outputtingToStream = false;
            m_outputOptions.SetOutputToMemory(true);

            m_format = m_compressionOptions.Format;
            m_mipChain = mipChain;

            try
            {
                return NvTextureToolsLibrary.Instance.Process(m_compressorPtr, m_inputOptionsPtr, m_compressionOptionsPtr, m_outputOptionsPtr);
            }
            finally
            {
                //Make sure we don't hold onto any references
                m_mipChain = null;
                m_currentMip = null;
            }
        }

        //BeginImageHandler callback
        private void BeginImage(int size, int width, int height, int depth, int face, int mipLevel)
        {
            if (m_outputtingToStream)
                return;

            //Nvtt treats texture2D's single face as PosX but we do differentiate in the API so make sure we set the face to none
            if (m_inputOptions.TextureType == TextureType.Texture2D && (CubeMapFace)face == CubeMapFace.Positive_X)
                face = (int)CubeMapFace.None;

            m_currentMip = new CompressedImageData(width, height, (CubeMapFace) face, m_format);
            m_currentBytePos = 0;
            m_mipChain.Add(m_currentMip);
        }

        //OutputHandler callback, if writing header, that usually is the first call before any begin-end image blocks
        private bool OutputData(IntPtr data, int size)
        {
            if (m_outputtingToStream)
            {
                int offset = 0;
                while(size > 0)
                {
                    int count = Math.Min(m_tempBuffer.Length, size);
                    size -= count;
                    
                    MemoryHelper.CopyMemory(m_pinnedTempBufferPtr, MemoryHelper.AddIntPtr(data, offset), count);
                    offset += count;

                    m_currentStream.Write(m_tempBuffer, 0, count);
                }
            }
            else
            {
                if (m_currentMip != null)
                {
                    MemoryHelper.CopyMemory(MemoryHelper.AddIntPtr(m_currentMip.DataPtr, m_currentBytePos), data, size);
                    m_currentBytePos += size;
                }
            }

            return true;
        }
        
        //EndImageHandler callback
        private void EndImage()
        {
            if (m_outputtingToStream)
                return;

            m_currentMip = null;
            m_currentBytePos = 0;
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);

            GC.SuppressFinalize(this);
        }

        private void Dispose(bool isDisposing)
        {
            if(!m_isDisposed)
            {
                NvTextureToolsLibrary lib = NvTextureToolsLibrary.Instance;

                if(isDisposing)
                {
                    //Make sure the function ptrs are set back to NULL
                    m_outputOptions.SetOutputToMemory(false);

                    m_inputOptions = null;
                    m_compressionOptions = null;
                    m_outputOptions = null;
                }

                //I think if we're getting called by the finalizer at the point that the lib is null, we're probably shutting down anyways...
                if(lib != null && lib.IsLibraryLoaded)
                {
                    if(m_compressorPtr != IntPtr.Zero)
                    {
                        lib.DestroyCompressor(m_compressorPtr);
                        m_compressorPtr = IntPtr.Zero;
                    }

                    if(m_inputOptionsPtr != IntPtr.Zero)
                    {
                        lib.DestroyInputOptions(m_inputOptionsPtr);
                        m_inputOptionsPtr = IntPtr.Zero;
                    }

                    if(m_compressionOptionsPtr != IntPtr.Zero)
                    {
                        lib.DestroyCompressionOptions(m_compressionOptionsPtr);
                        m_compressionOptionsPtr = IntPtr.Zero;
                    }

                    if(m_outputOptionsPtr != IntPtr.Zero)
                    {
                        lib.DestroyOutputOptions(m_outputOptionsPtr);
                        m_outputOptionsPtr = IntPtr.Zero;
                    }
                }

                m_isDisposed = true;
            }
        }

        /// <summary>
        /// Options for setting up input to a <see cref="Compressor"/>.
        /// </summary>
        /// <remarks>
        /// Input image data can be set from <see cref="Surface"/> objects or from raw IntPtr memory. There are some
        /// methods that can set both the texture layout and all the first mip surfaces in one call, but the user can optionally
        /// set the texture layout and then set each individual face and miplevel. In most cases the first mip level gets set
        /// and mipmaps are generated, but there are other cases where mipmaps are already present and the <see cref="Compressor"/>
        /// will use them during compression or normal map generation. If mipmaps aren't set explicitly and normal maps are generated, 
        /// the first mip is converted to a normal map, and mipmaps are generated from that.
        /// </remarks>
        public sealed class InputOptions
        {
            private IntPtr m_inputOptionsPtr;
            private TextureType m_type;
            private int m_width, m_height, m_depth;
            private AlphaMode m_alphaMode;
            private float m_inputGamma, m_outputGamma;
            private bool m_generateMipmaps;
            private int m_mipCount;
            private int m_maxLevel; //If value > 0 then the mip level count is explicitly set, otherwise it is automatically determined
            private MipmapFilter m_mipFilter;
            private float m_kaiserWidth, m_kaiserAlpha, m_kaiserStretch;
            private bool m_isNormalMap;
            private bool m_normalizeMipmaps;
            private bool m_convertToNormalMap;
            private float m_redScale, m_greenScale, m_blueScale, m_alphaScale;
            private float m_smallBumpFreqScale, m_mediumBumpFreqScale, m_bigBumpFreqScale, m_largeDumpFreqScale;
            private int m_maxExtent;
            private RoundMode m_roundMode;
            private WrapMode m_wrapMode;
            private List<bool> m_faceHasData;

            /// <summary>
            /// Gets the pointer to the native object.
            /// </summary>
            public IntPtr NativePtr
            {
                get
                {
                    return m_inputOptionsPtr;
                }
            }

            /// <summary>
            /// Gets the texture type of the input image.
            /// </summary>
            public TextureType TextureType
            {
                get
                {
                    return m_type;
                }
            }

            /// <summary>
            /// Gets the width of the input image.
            /// </summary>
            public int Width
            {
                get
                {
                    return m_width;
                }
            }

            /// <summary>
            /// Gets the height of the input image.
            /// </summary>
            public int Height
            {
                get
                {
                    return m_height;
                }
            }

            /// <summary>
            /// Gets the depth of the input image. (Typically 1 if not 3D).
            /// </summary>
            public int Depth
            {
                get
                {
                    return m_depth;
                }
            }

            /// <summary>
            /// Gets the number of faces in the input image. If a 2D image then this is always one, if
            /// a cubemap then this is 6 faces (one for each side of the cube).
            /// </summary>
            public int FaceCount
            {
                get
                {
                    return (m_type == TextureType.TextureCube) ? 6 : 1;
                }
            }

            /// <summary>
            /// Gets the number of mipmaps that will be generated for each face.
            /// </summary>
            public int MipmapCount
            {
                get
                {
                    return m_mipCount;
                }
            }

            /// <summary>
            /// Gets or sets if mipmaps should be generated. The max level of mips will be set if mipmaps
            /// are to be generated. By default this is true.
            /// </summary>
            public bool GenerateMipmaps
            {
                get
                {
                    return m_generateMipmaps;
                }
                set
                {
                    SetMipmapGeneration(value, -1);
                }
            }

            /// <summary>
            /// Gets or sets the alpha mode that will be used during processing. By default this is <see cref="AlphaMode.None"/>.
            /// </summary>
            public AlphaMode AlphaMode
            {
                get
                {
                    return m_alphaMode;
                }
                set
                {
                    m_alphaMode = value;
                    NvTextureToolsLibrary.Instance.SetInputOptionsAlphaMode(m_inputOptionsPtr, value);
                }
            }

            /// <summary>
            /// Gets or sets the round mode that will be used during processing. By default this is <see cref="RoundMode.None"/>.
            /// </summary>
            public RoundMode RoundMode
            {
                get
                {
                    return m_roundMode;
                }
                set
                {
                    m_roundMode = value;
                    NvTextureToolsLibrary.Instance.SetInputOptionsRoundMode(m_inputOptionsPtr, value);
                }
            }

            /// <summary>
            /// Gets or sets the maximum texture dimensions, used in conjunction with <see cref="RoundMode"/>. By
            /// default this is set to zero.
            /// </summary>
            public int MaxTextureExtent
            {
                get
                {
                    return m_maxExtent;
                }
                set
                {
                    m_maxExtent = value;
                    NvTextureToolsLibrary.Instance.SetInputOptionsMaxExtents(m_inputOptionsPtr, value);
                }
            }

            /// <summary>
            /// Gets or sets the filter used during mipmap generation. By default this is <see cref="MipmapFilter.Box"/>.
            /// </summary>
            public MipmapFilter MipmapFilter
            {
                get
                {
                    return m_mipFilter;
                }
                set
                {
                    m_mipFilter = value;
                    NvTextureToolsLibrary.Instance.SetInputOptionsMipmapFilter(m_inputOptionsPtr, value);
                }
            }

            /// <summary>
            /// Gets or sets the wrap mode used during mipmap generation. By default this is set to <see cref="WrapMode.Mirror"/>.
            /// </summary>
            public WrapMode WrapMode
            {
                get
                {
                    return m_wrapMode;
                }
                set
                {
                    m_wrapMode = value;
                    NvTextureToolsLibrary.Instance.SetInputOptionsWrapMode(m_inputOptionsPtr, value);
                }
            }

            /// <summary>
            /// Gets or sets if the input image is to be treated as a normal map. By default this is false.
            /// </summary>
            public bool IsNormalMap
            {
                get
                {
                    return m_isNormalMap;
                }
                set
                {
                    m_isNormalMap = value;

                    NvTextureToolsLibrary.Instance.SetInputOptionsNormalMap(m_inputOptionsPtr, value);
                }
            }

            /// <summary>
            /// Gets or sets if the mipmaps of a normal map should be renormalized after they are generated. By default
            /// this is true.
            /// </summary>
            public bool NormalizeMipmaps
            {
                get
                {
                    return m_normalizeMipmaps;
                }
                set
                {
                    m_normalizeMipmaps = value;
                    NvTextureToolsLibrary.Instance.SetInputOptionsNormalizeMipmaps(m_inputOptionsPtr, value);
                }
            }

            /// <summary>
            /// Gets or sets if the input image should be converted to a normal map.
            /// </summary>
            public bool ConvertToNormalMap
            {
                get
                {
                    return m_convertToNormalMap;
                }
                set
                {
                    m_convertToNormalMap = value;
                    NvTextureToolsLibrary.Instance.SetInputOptionsConvertToNormalMap(m_inputOptionsPtr, value);
                }
            }

            /// <summary>
            /// Gets if the input has mipmap data, this will be true/false depending on the success of the functions to set
            /// any mipmap data. All mipmap faces (1 for Texture2D, 6 for TextureCube) need to be set for the compressor to run.
            /// </summary>
            public bool HasData
            {
                get
                {
                    if (m_faceHasData.Count == 0)
                        return false;

                    for(int i = 0; i < m_faceHasData.Count; i++)
                    {
                        if (!m_faceHasData[i])
                            return false;
                    }

                    return true;
                }
            }

            internal InputOptions(IntPtr nativePtr)
            {
                m_inputOptionsPtr = nativePtr;

                m_type = TextureType.Texture2D;
                m_width = 0;
                m_height = 0;
                m_depth = 0;
                m_maxExtent = 0;
                m_alphaMode = AlphaMode.None;
                m_roundMode = RoundMode.None;
                m_wrapMode = WrapMode.Mirror;

                m_inputGamma = 2.2f;
                m_outputGamma = 2.2f;

                m_generateMipmaps = true;
                m_mipFilter = MipmapFilter.Box;
                m_mipCount = 1;
                m_maxLevel = -1;
                m_faceHasData = new List<bool>(6);

                m_kaiserWidth = 3;
                m_kaiserAlpha = 4.0f;
                m_kaiserStretch = 1.0f;

                m_isNormalMap = false;
                m_normalizeMipmaps = true;
                m_convertToNormalMap = false;
                m_redScale = 0f;
                m_greenScale = 0f;
                m_blueScale = 0f;
                m_alphaScale = 1.0f;

                float denom = 1.0f + 0.5f + 0.25f + 0.125f;
                m_smallBumpFreqScale = 1.0f / denom;
                m_mediumBumpFreqScale = 0.5f / denom;
                m_bigBumpFreqScale = 0.25f / denom;
                m_largeDumpFreqScale = 0.125f / denom;

                //Input format is always BGRA_8UB so don't expose it here
            }

            /// <summary>
            /// Sets the layout of the input image.
            /// </summary>
            /// <param name="type">Type of texture the input image is.</param>
            /// <param name="width">Width of the input image.</param>
            /// <param name="height">Height of the input image.</param>
            /// <param name="depth">Depth of the input image, by default this is 1.</param>
            public void SetTextureLayout(TextureType type, int width, int height, int depth = 1)
            {
                m_type = type;
                m_width = width;
                m_height = height;
                m_depth = depth;
                m_faceHasData.Clear();

                switch(type)
                {
                    case TextureType.Texture2D:
                        m_faceHasData.Add(false);
                        break;
                    case TextureType.TextureCube:
                        for (int i = 0; i < 6; i++)
                            m_faceHasData.Add(false);
                        break;
                }

                if(m_generateMipmaps && m_maxLevel == -1)
                    m_mipCount = MemoryHelper.CountMipmaps(width, height, depth);

                NvTextureToolsLibrary.Instance.SetInputOptionsTextureLayout(m_inputOptionsPtr, type, width, height, depth);
            }

            /// <summary>
            /// Clears the current input image data and texture layout. This frees any image data
            /// that the input options holds onto.
            /// </summary>
            public void ClearTextureLayout()
            {
                m_type = TextureType.Texture2D;
                m_width = 0;
                m_height = 0;
                m_depth = 0;
                m_faceHasData.Clear();

                NvTextureToolsLibrary.Instance.ResetInputOptionsTextureLayout(m_inputOptionsPtr);
            }

            /// <summary>
            /// Sets mipmap data as input. Format is always considered to be in 32-bit BGRA form. Don't forget to set the texture layout first otherwise this will error.
            /// </summary>
            /// <param name="data">Pointer to data.</param>
            /// <param name="width">Width of the image.</param>
            /// <param name="height">Height of the image.</param>
            /// <param name="depth">Depth of the image.</param>
            /// <param name="face">Cubemap face that the image corresponds to.</param>
            /// <param name="mipmapLevel">Mip level the image corresponds to.</param>
            /// <returns>True if the data was successfully set, false otherwise (e.g. does not match texture layout which needs to be set first).</returns>
            public bool SetMipmapData(IntPtr data, int width, int height, int depth, CubeMapFace face, int mipmapLevel)
            {
                return SetMipmapData(data, true, width, height, depth, face, mipmapLevel);
            }

            /// <summary>
            /// Sets mipmap data as input. Format is always considered to be in 32-bit BGRA form. Don't forget to set the texture layout first otherwise this will error.
            /// </summary>
            /// <param name="data">Pointer to data.</param>
            /// <param name="width">Width of the image.</param>
            /// <param name="height">Height of the image.</param>
            /// <param name="depth">Depth of the image.</param>
            /// <returns>True if the data was successfully set, false otherwise (e.g. does not match texture layout which needs to be set first).</returns>
            public bool SetMipmapData(IntPtr data, int width, int height, int depth = 1)
            {
                return SetMipmapData(data, true, width, height, depth, CubeMapFace.Positive_X, 0);
            }

            /// <summary>
            /// Sets mipmap data as input. Don't forget to set the texture layout first otherwise this will error.
            /// </summary>
            /// <param name="data">Pointer to data.</param>
            /// <param name="isBGRA">True if the data is in BGRA format, if false then RGBA. If false then the data is copied and converted to BGRA format.</param>
            /// <param name="width">Width of the image.</param>
            /// <param name="height">Height of the image.</param>
            /// <param name="depth">Depth of the image.</param>
            /// <returns>True if the data was successfully set, false otherwise (e.g. does not match texture layout which needs to be set first).</returns>
            public bool SetMipmapData(IntPtr data, bool isBGRA, int width, int height, int depth = 1)
            {
                return SetMipmapData(data, isBGRA, width, height, depth, CubeMapFace.Positive_X, 0);
            }

            /// <summary>
            /// Sets mipmap data as input. Don't forget to set the texture layout first otherwise this will error.
            /// </summary>
            /// <param name="data">Pointer to data.</param>
            /// <param name="isBGRA">True if the data is in BGRA format, if false then RGBA. If false then the data is copied and converted to BGRA format.</param>
            /// <param name="width">Width of the image.</param>
            /// <param name="height">Height of the image.</param>
            /// <param name="depth">Depth of the image.</param>
            /// <param name="face">Cubemap face that the image corresponds to.</param>
            /// <param name="mipmapLevel">Mip level the image corresponds to.</param>
            /// <returns>True if the data was successfully set, false otherwise (e.g. does not match texture layout which needs to be set first).</returns>
            public bool SetMipmapData(IntPtr data, bool isBGRA, int width, int height, int depth, CubeMapFace face, int mipmapLevel)
            {
                if(data == IntPtr.Zero)
                    return false;

                if (face == CubeMapFace.None)
                    face = CubeMapFace.Positive_X;

                IntPtr bgraPtr = data;
                bool needToDisposeBGRAPtr = false;

                if(!isBGRA)
                {
                    bgraPtr = ConvertToBGRA(data, width, height);
                    needToDisposeBGRAPtr = true;
                }

                bool succees = false;

                try
                {
                    succees = NvTextureToolsLibrary.Instance.SetInputOptionsMipmapData(m_inputOptionsPtr, bgraPtr, width, height, depth, (int)face, mipmapLevel);
                }
                finally
                {
                    SetHasData(face, succees);

                    if (needToDisposeBGRAPtr)
                        MemoryHelper.FreeMemory(bgraPtr);
                }

                return succees;
            }

            /// <summary>
            /// Sets mipmap data as input. Don't forget to set the texture layout first otherwise this will error.
            /// </summary>
            /// <param name="data">Bitmap surface. If not <see cref="ImageType.Bitmap"/> this fails, so be sure to convert it before calling.</param>
            /// <param name="face">Cubemap face to set surface to.</param>
            /// <param name="mipmapLevel">Mipmap level to set the surface to.</param>
            /// <returns>True if the data was successfully set, false otherwise (e.g. does not match texture layout which needs to be set first).</returns>
            public bool SetMipmapData(Surface data, CubeMapFace face, int mipmapLevel)
            {
                if(data == null || data.ImageType != ImageType.Bitmap)
                    return false;

                if (face == CubeMapFace.None)
                    face = CubeMapFace.Positive_X;

                //Ensure we are 32-bit bitmap
                Surface rgbaData = data;
                bool needToDispose = false;
                if(data.BitsPerPixel != 32)
                {
                    rgbaData = data.Clone();
                    if (!rgbaData.ConvertTo(ImageConversion.To32Bits))
                    {
                        rgbaData.Dispose();
                        return false;
                    }

                    needToDispose = true;
                }

                int width = rgbaData.Width;
                int height = rgbaData.Height;

                IntPtr bgraPtr = rgbaData.DataPtr;
                bool needToDisposeBGRAPtr = false;

                //Need to convert to BGRA since big endian has data in RGBA form
                if (!FreeImageLibrary.Instance.IsLittleEndian)
                {
                    bgraPtr = ConvertToBGRA(rgbaData.DataPtr, width, height);
                    needToDisposeBGRAPtr = true;
                }

                bool success = false;

                try
                {
                    success = NvTextureToolsLibrary.Instance.SetInputOptionsMipmapData(m_inputOptionsPtr, bgraPtr, width, height, 1, (int)face, mipmapLevel);
                }
                finally
                {
                    SetHasData(face, success);

                    if (needToDispose)
                        rgbaData.Dispose();

                    if (needToDisposeBGRAPtr)
                        MemoryHelper.FreeMemory(bgraPtr);
                }

                return success;      
            }

            private unsafe IntPtr ConvertToBGRA(IntPtr rgbaPtr, int width, int height)
            {
                IntPtr bgraPtr = MemoryHelper.AllocateMemory(4 * width * height);

                byte* pBGRA = (byte*)bgraPtr.ToPointer();
                byte* pRGBA = (byte*)rgbaPtr.ToPointer();

                int totalTexels = width * height;

                for(int i = 0; i < totalTexels; i++)
                {
                    //RGBA -> BGRA
                    pBGRA[0] = pRGBA[2];
                    pBGRA[1] = pRGBA[1];
                    pBGRA[2] = pRGBA[0];
                    pBGRA[3] = pRGBA[3];

                    pBGRA += 4;
                    pRGBA += 4;
                }

                return bgraPtr;
            }

            /// <summary>
            /// Sets input data from a specified surface. This sets the texture layout as a 2D texture
            /// and the first mipmap with the surface data.
            /// </summary>
            /// <param name="data">Bitmap surface data.</param>
            /// <returns>True if the operation was successful, false otherwise.</returns>
            public bool SetData(Surface data)
            {
                if (data == null || data.ImageType != ImageType.Bitmap)
                    return false;

                SetTextureLayout(TextureType.Texture2D, data.Width, data.Height, 1);

                bool success = SetMipmapData(data, CubeMapFace.Positive_X, 0);

                if (!success)
                    ClearTextureLayout();

                return success;
            }

            /// <summary>
            /// Sets input data from an array of surfaces representing a cubemap (6 surfaces total). This sets the texture layout as a 
            /// cubemap and sets each surface as the first mipmap of each face. All the surface dimensions must match, and there must be
            /// six faces.
            /// </summary>
            /// <param name="cubeFaces">Array of bitmap surfaces, in the order of the CubeMapFace enum (first index is PosX, then NegX, etc).</param>
            /// <returns>True if the operation was successful, false otherwise.</returns>
            public bool SetData(Surface[] cubeFaces)
            {
                if (cubeFaces == null || cubeFaces.Length != 6)
                    return false;

                Surface first = cubeFaces[0];

                if (first == null || first.ImageType != ImageType.Bitmap)
                    return false;

                for(int i = 1; i < cubeFaces.Length; i++)
                {
                    Surface next = cubeFaces[i];
                    if (next == null || next.ImageType != ImageType.Bitmap)
                        return false;

                    if (first.Width != next.Width || first.Height != next.Height)
                        return false;
                }

                SetTextureLayout(TextureType.TextureCube, first.Width, first.Height, 1);

                for (int i = 0; i < cubeFaces.Length; i++)
                {
                    //Set each cubemap face, if errors then reset and return
                    if(!SetMipmapData(cubeFaces[i], (CubeMapFace)i, 0))
                    {
                        ClearTextureLayout();
                        return false;
                    }
                }

                return true;
            }

            /// <summary>
            /// Sets the filter parameters used during normal map generation.
            /// </summary>
            /// <param name="small">Small parameter.</param>
            /// <param name="medium">Medium parameter.</param>
            /// <param name="big">Big parameter.</param>
            /// <param name="large">Large parameter.</param>
            public void SetNormalFilter(float small, float medium, float big, float large)
            {
                float total = small + medium + big + large;

                m_smallBumpFreqScale = small / total;
                m_mediumBumpFreqScale = medium / total;
                m_bigBumpFreqScale = big / total;
                m_largeDumpFreqScale = large / total;

                NvTextureToolsLibrary.Instance.SetInputOptionsNormalFilter(m_inputOptionsPtr, small, medium, big, large);
            }

            /// <summary>
            /// Gets the filter parameters used during normal map generation.
            /// </summary>
            /// <param name="small">Small parameter.</param>
            /// <param name="medium">Medium parameter.</param>
            /// <param name="big">Big parameter.</param>
            /// <param name="large">Large parameter.</param>
            public void GetNormalFilter(out float small, out float medium, out float big, out float large)
            {
                small = m_smallBumpFreqScale;
                medium = m_mediumBumpFreqScale;
                big = m_bigBumpFreqScale;
                large = m_largeDumpFreqScale;
            }

            /// <summary>
            /// Sets gamma correction parameters. By default both are set to 2.2 and they are only
            /// applied to the RGB channels and never applied to normal maps. Gamma can be disabled by setting both
            /// values to 1.0.
            /// </summary>
            /// <param name="inputGamma">Input gamma</param>
            /// <param name="outputGamma">Output gamma</param>
            public void SetGamma(float inputGamma, float outputGamma)
            {
                m_inputGamma = inputGamma;
                m_outputGamma = outputGamma;

                NvTextureToolsLibrary.Instance.SetInputOptionsGamma(m_inputOptionsPtr, m_inputGamma, m_outputGamma);
            }

            /// <summary>
            /// Gets gamma correction parameters.
            /// </summary>
            /// <param name="inputGamma">Input gamma</param>
            /// <param name="outputGamma">Output gamma</param>
            public void GetGamma(out float inputGamma, out float outputGamma)
            {
                inputGamma = m_inputGamma;
                outputGamma = m_outputGamma;
            }

            /// <summary>
            /// Sets mipmap generation options. The number of mipmaps can be explicitly set.
            /// </summary>
            /// <param name="generateMips">True if mipmaps should be generated, false otherwise.</param>
            /// <param name="maxLevel">Optional max mip level, if -1 then the full mipmap count is determined.</param>
            public void SetMipmapGeneration(bool generateMips, int maxLevel = -1)
            {
                m_generateMipmaps = generateMips;
                m_mipCount = 1;
                m_maxLevel = maxLevel;

                if (maxLevel == -1)
                    m_mipCount = MemoryHelper.CountMipmaps(m_width, m_height, m_depth);
                else
                    m_mipCount = maxLevel;

                NvTextureToolsLibrary.Instance.SetInputOptionsMipmapGeneration(m_inputOptionsPtr, generateMips, maxLevel);
            }

            /// <summary>
            /// Sets kaiser filter parameters when the mipmap filter is set to <see cref="MipmapFilter.Kaiser"/>.
            /// </summary>
            /// <param name="width">Width parameter, default is 3.0f.</param>
            /// <param name="alpha">Alpha parameter, default is 4.0f.</param>
            /// <param name="stretch">Stretch parameter, default is 1.0f.</param>
            public void SetKaiserParameters(float width = 3.0f, float alpha = 4.0f, float stretch = 1.0f)
            {
                m_kaiserWidth = width;
                m_kaiserAlpha = alpha;
                m_kaiserStretch = stretch;

                NvTextureToolsLibrary.Instance.SetInputOptionsKaiserParameters(m_inputOptionsPtr, width, alpha, stretch);
            }

            /// <summary>
            /// Gets kaiser filter parameters.
            /// </summary>
            /// <param name="width">Width parameter.</param>
            /// <param name="alpha">Alpha parameter.</param>
            /// <param name="stretch">Stretch parameter.</param>
            public void GetKaiserParameters(out float width, out float alpha, out float stretch)
            {
                width = m_kaiserWidth;
                alpha = m_kaiserAlpha;
                stretch = m_kaiserStretch;
            }

            /// <summary>
            /// Sets height evaluation parameters for use in normal map generation. The height factors do not
            /// necessarily sum to one.
            /// </summary>
            /// <param name="redScale">Scale for the red channel.</param>
            /// <param name="greenScale">Scale for the green channel.</param>
            /// <param name="blueScale">Scale for the blue channel.</param>
            /// <param name="alphaScale">Scale for the alpha channel.</param>
            public void SetHeightEvaluation(float redScale, float greenScale, float blueScale, float alphaScale)
            {
                m_redScale = redScale;
                m_greenScale = greenScale;
                m_blueScale = blueScale;
                m_alphaScale = alphaScale;

                NvTextureToolsLibrary.Instance.SetInputOptionsHeightEvaluation(m_inputOptionsPtr, redScale, greenScale, blueScale, alphaScale);
            }

            /// <summary>
            /// Gets height evaluation parameters.
            /// </summary>
            /// <param name="redScale">Scale for the red channel.</param>
            /// <param name="greenScale">Scale for the green channel.</param>
            /// <param name="blueScale">Scale for the blue channel.</param>
            /// <param name="alphaScale">Scale for the alpha channel.</param>
            public void GetHeightEvaluation(out float redScale, out float greenScale, out float blueScale, out float alphaScale)
            {
                redScale = m_redScale;
                greenScale = m_greenScale;
                blueScale = m_blueScale;
                alphaScale = m_alphaScale;
            }

            private void SetHasData(CubeMapFace face, bool success)
            {
                int idx = (int)face;

                if (idx >= m_faceHasData.Count)
                    return;

                m_faceHasData[idx] = success;
            }
        }

        /// <summary>
        /// Options for configuring how data is written in a <see cref="Compressor"/>.
        /// </summary>
        public sealed class CompressionOptions
        {
            private IntPtr m_compressionOptionsPtr;
            private CompressionFormat m_format;
            private CompressionQuality m_quality;
            private float m_rColorWeight, m_gColorWeight, m_bColorWeight, m_aColorWeight;
            private uint m_bitCount;
            private uint m_rMask, m_gMask, m_bMask, m_aMask;
            private bool m_enableColorDithering, m_enableAlphaDithering, m_binaryAlpha;
            private int m_alphaThreshold;

            /// <summary>
            /// Gets the pointer to the native object.
            /// </summary>
            public IntPtr NativePtr
            {
                get
                {
                    return m_compressionOptionsPtr;
                }
            }

            /// <summary>
            /// Gets or sets the pixel format that images will be outputted as.
            /// </summary>
            public CompressionFormat Format
            {
                get
                {
                    return m_format;
                }
                set
                {
                    m_format = value;
                    NvTextureToolsLibrary.Instance.SetCompressionOptionsFormat(m_compressionOptionsPtr, value);
                }
            }
            
            /// <summary>
            /// Gets or sets the compression quality.
            /// </summary>
            public CompressionQuality Quality
            {
                get
                {
                    return m_quality;
                }
                set
                {
                    m_quality = value;
                    NvTextureToolsLibrary.Instance.SetCompressionOptionsQuality(m_compressionOptionsPtr, value);
                }
            }

            internal CompressionOptions(IntPtr nativePtr)
            {
                m_compressionOptionsPtr = nativePtr;

                //Setup defaults
                m_format = CompressionFormat.BC1;
                m_quality = CompressionQuality.Normal;
                m_bitCount = 32;
                m_bMask = 0x000000FF;
                m_gMask = 0x0000FF00;
                m_rMask = 0x00FF0000;
                m_aMask = 0xFF000000;

                m_rColorWeight = 1.0f;
                m_gColorWeight = 1.0f;
                m_bColorWeight = 1.0f;
                m_aColorWeight = 1.0f;

                m_enableColorDithering = false;
                m_enableAlphaDithering = false;
                m_binaryAlpha = false;
                m_alphaThreshold = 127;
            }

            /// <summary>
            /// Sets the color output format if no block compression is set (up to 32 bit RGBA). For example, to convert to RGB 5:6:5 format,
            /// <code>SetPixelFormat(16, 0x001F, 0x07E0, 0xF800, 0)</code>.
            /// </summary>
            /// <param name="bitsPerPixel">Bits per pixel of the color format.</param>
            /// <param name="red_mask">Mask for the bits that correspond to the red channel.</param>
            /// <param name="green_mask">Mask for the bits that correspond to the green channel.</param>
            /// <param name="blue_mask">Mask for the bits that correspond to the blue channel.</param>
            /// <param name="alpha_mask">Mask for the bits that correspond to the alpha channel.</param>
            public void SetPixelFormat(uint bitsPerPixel, uint red_mask, uint green_mask, uint blue_mask, uint alpha_mask)
            {
                m_bitCount = bitsPerPixel;
                m_rMask = red_mask;
                m_gMask = green_mask;
                m_bMask = blue_mask;
                m_aMask = alpha_mask;

                NvTextureToolsLibrary.Instance.SetCompressionOptionsPixelFormat(m_compressionOptionsPtr, bitsPerPixel, red_mask, green_mask, blue_mask, alpha_mask);
            }

            /// <summary>
            /// Sets the color output format if no block compression is set to RGBA format rather than BGRA format. Essentially this sets the
            /// masks so red and blue values are swapped.
            /// </summary>
            public void SetRGBAPixelFormat()
            {
                uint alphaMask = 0xFF000000;
                uint blueMask = 0xFF0000;
                uint greenMask = 0xFF00;
                uint redMask = 0xFF;

                SetPixelFormat(32, redMask, greenMask, blueMask, alphaMask);
            }

            /// <summary>
            /// Sets the color output format if no block compression to the default BGRA format.
            /// </summary>
            public void SetBGRAPixelFormat()
            {
                uint alphaMask = 0xFF000000;
                uint redMask = 0xFF0000;
                uint greenMask = 0xFF00;
                uint blueMask = 0xFF;

                SetPixelFormat(32, redMask, greenMask, blueMask, alphaMask);
            }

            /// <summary>
            /// Gets the color output format if no compression is used.
            /// </summary>
            /// <param name="bitsPerPixel">Bits per pixel of the color format.</param>
            /// <param name="red_mask">Mask for the bits that correspond to the red channel.</param>
            /// <param name="green_mask">Mask for the bits that correspond to the green channel.</param>
            /// <param name="blue_mask">Mask for the bits that correspond to the blue channel.</param>
            /// <param name="alpha_mask">Mask for the bits that correspond to the alpha channel.</param>
            public void GetPixelFormat(out uint bitsPerPixel, out uint red_mask, out uint green_mask, out uint blue_mask, out uint alpha_mask)
            {
                bitsPerPixel = m_bitCount;
                red_mask = m_rMask;
                green_mask = m_gMask;
                blue_mask = m_bMask;
                alpha_mask = m_aMask;
            }

            /// <summary>
            /// Sets whether the compressor should do dithering before compression
            /// or during quantiziation. When using block compression this does not generally improve
            /// the quality of the output image, but in some cases it can produce smoother results. It is
            /// generally a good idea to enable dithering when the output format is RGBA color.
            /// </summary>
            /// <param name="enableColorDithering">True to enable color dithering, false otherwise.</param>
            /// <param name="enableAlphaDithering">True to enable alpha dithering false otherwise.</param>
            /// <param name="binaryAlpha">True to use binary alpha, false otherwise.</param>
            /// <param name="alphaThreshold">Alpha threshold, default is 127.</param>
           public void SetQuantization(bool enableColorDithering, bool enableAlphaDithering, bool binaryAlpha, int alphaThreshold = 127)
            {
                m_enableColorDithering = enableColorDithering;
                m_enableAlphaDithering = enableAlphaDithering;
                m_binaryAlpha = binaryAlpha;
                m_alphaThreshold = alphaThreshold;

                NvTextureToolsLibrary.Instance.SetCompressionOptionsQuantization(m_compressionOptionsPtr, enableColorDithering, enableAlphaDithering, binaryAlpha, alphaThreshold);
            }

            /// <summary>
            /// Gets the current quantiziation parameters.
            /// </summary>
            /// <param name="enableColorDithering">True to enable color dithering, false otherwise.</param>
            /// <param name="enableAlphaDithering">True to enable alpha dithering false otherwise.</param>
            /// <param name="binaryAlpha">True to use binary alpha, false otherwise.</param>
            /// <param name="alphaThreshold">Alpha threshold.</param>
            public void GetQuantization(out bool enableColorDithering, out bool enableAlphaDithering, out bool binaryAlpha, out int alphaThreshold)
            {
                enableColorDithering = m_enableColorDithering;
                enableAlphaDithering = m_enableAlphaDithering;
                binaryAlpha = m_binaryAlpha;
                alphaThreshold = m_alphaThreshold;
            }

            /// <summary>
            /// Sets color weighting during compression. By default the compression error is measured for each channel
            /// uniformly, but for some images it may make more sense to measure the error in a perceptual color space.
            /// </summary>
            /// <param name="red_weight">Weight for the red channel, default is 1.0.</param>
            /// <param name="green_weight">Weight for the green channel, default is 1.0.</param>
            /// <param name="blue_weight">Weight for the blue channel, default is 1.0.</param>
            /// <param name="alpha_weight">Weight for the alpha channel, default is 1.0.</param>
            public void SetColorWeights(float red_weight, float green_weight, float blue_weight, float alpha_weight)
            {
                m_rColorWeight = red_weight;
                m_gColorWeight = green_weight;
                m_bColorWeight = blue_weight;
                m_aColorWeight = alpha_weight;

                NvTextureToolsLibrary.Instance.SetCompressionOptionsColorWeights(m_compressionOptionsPtr, red_weight, green_weight, blue_weight, alpha_weight);
            }

            /// <summary>
            /// Gets the current color weights.
            /// </summary>
            /// <param name="red_weight">Weight for the red channel.</param>
            /// <param name="green_weight">Weight for the green channel.</param>
            /// <param name="blue_weight">Weight for the blue channel.</param>
            /// <param name="alpha_weight">Weight for the alpha channel.</param>
            public void GetColorWeights(out float red_weight, out float green_weight, out float blue_weight, out float alpha_weight)
            {
                red_weight = m_rColorWeight;
                green_weight = m_gColorWeight;
                blue_weight = m_bColorWeight;
                alpha_weight = m_aColorWeight;
            }
        }

        /// <summary>
        /// Options for how data is outputted when a <see cref="Compressor"/> is executing.
        /// </summary>
        public sealed class OutputOptions
        {
            private IntPtr m_outputOptionsPtr;
            private BeginImageHandler m_beginCallback;
            private OutputHandler m_outputCallback;
            private EndImageHandler m_endCallback;
            private IntPtr m_beginPtr, m_outputPtr, m_endPtr;
            private bool m_outputToMemory;
            private bool m_outputHeader;

            /// <summary>
            /// Gets the pointer to the native object.
            /// </summary>
            public IntPtr NativePtr
            {
                get
                {
                    return m_outputOptionsPtr;
                }
            }

            /// <summary>
            /// Gets or sets if the output container (e.g. DDS file format) header is written out before
            /// any image data. By default this is true.
            /// </summary>
            public bool OutputHeader
            {
                get
                {
                    return m_outputHeader;
                }
                set
                {
                    m_outputHeader = value;
                    NvTextureToolsLibrary.Instance.SetOutputOptionsOutputHeader(m_outputOptionsPtr, value);
                }
            }

            internal OutputOptions(IntPtr nativePtr, BeginImageHandler beginCallback, OutputHandler outputCallback, EndImageHandler endCallback)
            {
                m_outputOptionsPtr = nativePtr;

                m_beginCallback = beginCallback;
                m_outputCallback = outputCallback;
                m_endCallback = endCallback;

                m_beginPtr = Marshal.GetFunctionPointerForDelegate(beginCallback);
                m_outputPtr = Marshal.GetFunctionPointerForDelegate(outputCallback);
                m_endPtr = Marshal.GetFunctionPointerForDelegate(m_endCallback);

                m_outputToMemory = false;
                m_outputHeader = true; //API says the write handler will output the texture file header by default
            }

            internal void SetOutputToFile(String fileName)
            {
                SetOutputToMemory(false);

                NvTextureToolsLibrary.Instance.SetOutputOptionsFileName(m_outputOptionsPtr, fileName);
            }

            internal void SetOutputToMemory(bool toMemory)
            {
                if (m_outputToMemory == toMemory)
                    return;

                m_outputToMemory = toMemory;

                if (toMemory)
                    NvTextureToolsLibrary.Instance.SetOutputOptionsOutputHandler(m_outputOptionsPtr, m_beginPtr, m_outputPtr, m_endPtr);
                else
                    NvTextureToolsLibrary.Instance.SetOutputOptionsOutputHandler(m_outputOptionsPtr, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
        }
    }
}
