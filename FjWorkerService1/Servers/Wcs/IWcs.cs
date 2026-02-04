using FjWorkerService1.Models.Dws;
using FjWorkerService1.Models.Wcs;

namespace FjWorkerService1.Servers.Wcs {

    public interface IWcs {

        /// <summary>
        /// 扫描
        /// </summary>
        /// <param name="parcelId"></param>
        /// <param name="barcode"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task<WcsApiResponse> ScanParcelAsync(
            long parcelId,
            string barcode,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 上传
        /// </summary>
        /// <param name="parcelId"></param>
        /// <param name="dwsData"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task<WcsApiResponse> RequestChuteAsync(
            long parcelId,
            DwsData dwsData,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 落格
        /// </summary>
        /// <param name="parcelId"></param>
        /// <param name="chuteId"></param>
        /// <param name="barcode"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task<WcsApiResponse> NotifyChuteLandingAsync(
            long parcelId,
            string chuteId,
            string barcode,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 上传图片
        /// </summary>
        /// <param name="barcode"></param>
        /// <param name="imageData"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task<WcsApiResponse> UploadImageAsync(
            string barcode,
            byte[] imageData,
            CancellationToken cancellationToken = default);
    }
}
