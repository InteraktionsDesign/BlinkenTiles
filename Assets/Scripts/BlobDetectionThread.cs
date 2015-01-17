﻿using System;
using Emgu.CV;
using Emgu.CV.Structure;
using Emgu.CV.CvEnum;
using System.Drawing;

public class BlobDetectionThread
{
    private volatile BlobDetectionSettings _detectionSettings;

    private readonly KinectManager _depthManager;
    private readonly TileController _tileCtrl;

    private readonly RenderDataCallback _renderImageCallback;

    private volatile bool _shouldStop;
    private volatile bool _updatedData;

    private volatile int _runCounter;

    public BlobDetectionThread(KinectManager depthManager, TileController tileCtrl,
        BlobDetectionSettings detectionSettings, RenderDataCallback renderImageCallback)
    {
        _depthManager = depthManager;
        _tileCtrl = tileCtrl;
        _detectionSettings = detectionSettings;

        _renderImageCallback = renderImageCallback;

        _shouldStop = false;
        _updatedData = false;

        _runCounter = 0;
    }

    public void SetDetectionSettings(BlobDetectionSettings detectionSettings)
    {
        _detectionSettings = detectionSettings;
    }

    public void SetUpdatedData()
    {
        _updatedData = true;
    }

    public bool GetUpdatedData()
    {
        return _updatedData;
    }

    public int GetRunCount()
    {
        return _runCounter;
    }

    public void RequestStop()
    {
        _shouldStop = true;
    }

    private byte[] PrepareRenderImage(Image<Gray, byte> img)
    {
        return PrepareRenderImage(img.Convert<Rgb, byte>());
    }

    private unsafe byte[] PrepareRenderImage(Image<Rgb, byte> img)
    {
        var linData = new byte[img.Data.Length];
        Buffer.BlockCopy(img.Data, 0, linData, 0, img.Data.Length);
        return linData;
    }

    public unsafe void ProcessImg()
    {
        while (!_shouldStop)
        {
            while (!_updatedData && !_shouldStop)
            {
                // wait for next kinect data
            }

            if (_shouldStop) return;

            lock (_depthManager.DepthData)
            {
                fixed (ushort* dataPtr = _depthManager.DepthData)
                {
                    var dWidth = _depthManager.DepthWidth;
                    var dHeight = _depthManager.DepthHeight;

                    var depthImg = new Image<Gray, short>(dWidth, dHeight, 1024, new IntPtr(dataPtr));
                    var depthImgNorm = new Image<Gray, short>(dWidth, dHeight);

                    var filteredImg = depthImg.ThresholdToZero(new Gray(_detectionSettings.MinDepth));
                    filteredImg = filteredImg.ThresholdToZeroInv(new Gray(_detectionSettings.MaxDepth));
                    filteredImg = filteredImg.Sub(new Gray(_detectionSettings.MinDepth));
                    filteredImg = filteredImg.ThresholdToZero(new Gray(0));

                    var filteredImgNorm = new Image<Gray, short>(dWidth, dHeight);

                    CvInvoke.cvNormalize(depthImg, depthImgNorm, 0, short.MaxValue, NORM_TYPE.CV_MINMAX, IntPtr.Zero);
                    CvInvoke.cvNormalize(filteredImg, filteredImgNorm, 0, short.MaxValue, NORM_TYPE.CV_MINMAX,
                        IntPtr.Zero);

                    var imgOrg = depthImgNorm.Convert<Rgb, byte>();
                    var imgGray = filteredImgNorm.Convert<Gray, byte>();

                    var renderImage = new byte[0];

                    if (_detectionSettings.RenderImgType == 1)
                        renderImage = PrepareRenderImage(imgOrg);

                    if (_detectionSettings.RenderImgType == 2)
                        renderImage = PrepareRenderImage(imgGray);

                    // noise reduction
                    var imgSm = imgGray.PyrDown().PyrUp().SmoothGaussian(3);

                    var element = new StructuringElementEx(5, 5, 2, 2, CV_ELEMENT_SHAPE.CV_SHAPE_ELLIPSE);
                    CvInvoke.cvErode(imgSm, imgSm, element, 2);
                    CvInvoke.cvDilate(imgSm, imgSm, element, 2);

                    // filtering
                    var imgThr = imgSm.InRange(new Gray(_detectionSettings.MinThreshold),
                        new Gray(_detectionSettings.MaxThreshold));

                    if (_detectionSettings.RenderImgType == 3)
                        renderImage = PrepareRenderImage(imgThr);

                    // create grid
                    var cols = (int) _detectionSettings.GridSize.x;
                    var rows = (int) _detectionSettings.GridSize.y;
                    var objGrid = new bool[cols, rows];

                    for (int x = 0; x < cols; x++)
                        for (int y = 0; y < rows; y++)
                            objGrid[x, y] = false;

                    // find contours
                    var gridLoc = _detectionSettings.GridLoc;
                    var fieldSize = _detectionSettings.FieldSize;
                    var fieldTol = _detectionSettings.FieldTolerance;

                    using (MemStorage storage = new MemStorage())
                    {
                        for (var contours = imgThr.FindContours(CHAIN_APPROX_METHOD.CV_CHAIN_APPROX_SIMPLE,
                            RETR_TYPE.CV_RETR_TREE, storage);
                            contours != null;
                            contours = contours.HNext)
                        {
                            Contour<Point> currentContour = contours.ApproxPoly(contours.Perimeter*0.015, storage);

                            var bdRect = currentContour.BoundingRectangle;
                            if (bdRect.Width < 20 || bdRect.Height < 20)
                                continue;

                            // check against grid
                            for (int x = 0; x < cols; x++)
                            {
                                for (int y = 0; y < rows; y++)
                                {
                                    var grRect = new Rectangle(
                                        (int) (gridLoc.x + x*fieldSize.x + fieldTol.x),
                                        (int) (gridLoc.y + y*fieldSize.y + fieldTol.y),
                                        (int) (fieldSize.x - 2*fieldTol.x),
                                        (int) (fieldSize.y - 2*fieldTol.y));

                                    imgOrg.Draw(grRect, new Rgb(0, 0, 200), 2);

                                    if (bdRect.IntersectsWith(grRect))
                                        objGrid[x, y] = true;
                                }
                            }

                            imgOrg.Draw(currentContour.BoundingRectangle, new Rgb(0, 255, 255), 2);
                        }
                    }

                    // set tile status
					//check if tilematrix is empty (= no dudes on the field)
					var isOccupied = false;
	                for (int x = 0; x < cols; x++)
	                	for (int y = 0; y < rows; y++) {
	                    	_tileCtrl.SetTileStatus(cols - x - 1, rows - y - 1, objGrid[x, y]);
							isOccupied |= objGrid[x, y]; 
						}
					Config.MatrixEmpty = !isOccupied;
                    if (_detectionSettings.RenderImgType == 4)
                        renderImage = PrepareRenderImage(imgOrg);

                    // draw grid
                    var blendImg = new Image<Rgb, byte>(imgOrg.Width, imgOrg.Height);

                    for (int x = 0; x < cols; x++)
                    {
                        for (int y = 0; y < rows; y++)
                        {
                            var grRect = new Rectangle(
                                (int) (gridLoc.x + x*fieldSize.x),
                                (int) (gridLoc.y + y*fieldSize.y),
                                (int) fieldSize.x,
                                (int) fieldSize.y);

                            blendImg.Draw(grRect, new Rgb(0, 255, 0), objGrid[x, y] ? -1 : 2);
                            imgOrg.Draw(grRect, new Rgb(0, 0, 200), 2);
                        }
                    }

                    imgOrg = imgOrg.AddWeighted(blendImg, 0.7f, 0.3f, 0);

                    if (_detectionSettings.RenderImgType == 5)
                        renderImage = PrepareRenderImage(imgOrg);

                    if (_detectionSettings.RenderImgType == 6)
                    {
                        lock (_depthManager.ColorImage)
                        {
                            fixed (byte* dataPtr2 = _depthManager.ColorImage)
                            {
                                var colorImg = new Image<Rgb, byte>(dWidth, dHeight, 1536, new IntPtr(dataPtr2));
                                imgOrg = imgOrg.AddWeighted(colorImg, 0.5f, 0.5f, 0);
                                renderImage = PrepareRenderImage(imgOrg);
                            }
                        }
                    }

                    if (_renderImageCallback != null && renderImage.Length > 0)
                        _renderImageCallback(renderImage);

                    // wait for new data
                    _updatedData = false;
                    _runCounter++;
                }
            }
        }
    }
}