﻿using Dispose.Scope;
using Masuit.MyBlogs.Core.Extensions;
using Masuit.Tools.AspNetCore.ModelBinder;
using Masuit.Tools.AspNetCore.ResumeFileResults.Extensions;
using Masuit.Tools.Excel;
using Microsoft.Net.Http.Headers;
using System.Net;
using System.Text.RegularExpressions;
using Masuit.MyBlogs.Core.Models;
using Masuit.Tools.Core;
using Masuit.Tools.Mime;

namespace Masuit.MyBlogs.Core.Controllers;

[Route("partner/[action]")]
public sealed class AdvertisementController : BaseController
{
    public IAdvertisementClickRecordService ClickRecordService { get; set; }

    /// <summary>
    /// 前往
    /// </summary>
    /// <param name="id">广告id</param>
    /// <returns></returns>
    [HttpGet("/p{id:int}"), HttpGet("{id:int}", Order = 1), ResponseCache(Duration = 3600)]
    public async Task<IActionResult> Redirect(int id)
    {
        var ad = await AdsService.GetAsync(a => a.Id == id && a.Status == Status.Available) ?? throw new NotFoundException("推广链接不存在");
        if (!Request.IsRobot() && string.IsNullOrEmpty(HttpContext.Session.Get<string>("ads" + id)))
        {
            HttpContext.Session.Set("ads" + id, id.ToString());
            ClickRecordService.AddEntity(new AdvertisementClickRecord()
            {
                IP = ClientIP.ToString(),
                Location = Request.Location(),
                Referer = Request.Headers[HeaderNames.Referer].ToString(),
                Time = DateTime.Now,
                AdvertisementId = id
            });
            await ClickRecordService.SaveChangesAsync();
            var start = DateTime.Today.AddYears(-1);
            await ClickRecordService.GetQuery(a => a.Time < start).ExecuteDeleteAsync();
        }

        return Redirect(ad.Url);
    }

    /// <summary>
    /// 获取分页
    /// </summary>
    /// <returns></returns>
    [MyAuthorize]
    public ActionResult GetPageData(int page = 1, [Range(1, int.MaxValue, ErrorMessage = "页大小必须大于0")] int size = 10, string kw = "", AdvertiseOrderBy orderBy = AdvertiseOrderBy.Default)
    {
        Expression<Func<Advertisement, bool>> where = p => true;
        if (!string.IsNullOrEmpty(kw))
        {
            kw = Regex.Escape(kw);
            where = where.And(p => Regex.IsMatch(p.Title + p.Description + p.Url, kw, RegexOptions.IgnoreCase));
        }

        var queryable = AdsService.GetQuery(where).ProjectViewModel().OrderByDescending(p => p.Status == Status.Available);
        switch (orderBy)
        {
            case AdvertiseOrderBy.Price:
                queryable = queryable.ThenByDescending(a => a.Price).ThenByDescending(a => a.Id);
                break;

            case AdvertiseOrderBy.DisplayCount:
                queryable = queryable.ThenByDescending(a => a.DisplayCount);
                break;

            case AdvertiseOrderBy.ViewCount:
                queryable = queryable.ThenByDescending(a => a.ViewCount);
                break;

            case AdvertiseOrderBy.AverageViewCount:
                queryable = queryable.ThenByDescending(a => a.AverageViewCount);
                break;

            case AdvertiseOrderBy.ClickRate:
                queryable = queryable.ThenByDescending(a => a.ViewCount * 1m / (a.DisplayCount + 1));
                break;

            default:
                queryable = queryable.ThenByDescending(a => a.UpdateTime);
                break;
        }

        var list = queryable.ToPagedList(page, size);
        return Ok(list);
    }

    /// <summary>
    /// 保存广告
    /// </summary>
    /// <param name="model"></param>
    /// <returns></returns>
    [HttpPost, MyAuthorize, DistributedLockFilter]
    public async Task<IActionResult> Save([FromBodyOrDefault] AdvertisementDto model)
    {
        var entity = AdsService[model.Id] ?? new Advertisement();
        model.CategoryIds = model.CategoryIds?.Replace("null", "");
        model.Regions = Regex.Replace(model.Regions ?? "", @"(\p{P}|\p{Z}|\p{S})+", "|");
        if (model.RegionMode == RegionLimitMode.All)
        {
            model.Regions = null;
        }

        if (model.Types.Contains(AdvertiseType.Banner.ToString("D")) && string.IsNullOrEmpty(model.ImageUrl))
        {
            return ResultData(null, false, "宣传大图不能为空");
        }

        if (model.Types.Length > 3 && string.IsNullOrEmpty(model.ThumbImgUrl))
        {
            return ResultData(null, false, "宣传小图不能为空");
        }

        model.Update(entity);
        var b = await AdsService.AddOrUpdateSavedAsync(a => a.Id, entity) > 0;
        return ResultData(null, b, b ? "保存成功" : "保存失败");
    }

    /// <summary>
    /// 删除广告
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [HttpPost("{id}"), HttpGet("{id}"), MyAuthorize]
    public async Task<IActionResult> Delete(int id)
    {
        bool b = await AdsService.DeleteByIdAsync(id) > 0;
        return ResultData(null, b, b ? "删除成功" : "删除失败");
    }

    /// <summary>
    /// 广告上下架
    /// </summary>
    /// <param name="id">文章id</param>
    /// <returns></returns>
    [MyAuthorize, HttpPost("{id}"), DistributedLockFilter]
    public async Task<ActionResult> ChangeState(int id)
    {
        var ad = await AdsService.GetByIdAsync(id) ?? throw new NotFoundException("广告不存在！");
        ad.Status = ad.Status == Status.Available ? Status.Unavailable : Status.Available;
        return ResultData(null, await AdsService.SaveChangesAsync() > 0, ad.Status == Status.Available ? $"【{ad.Title}】已上架！" : $"【{ad.Title}】已下架！");
    }

    /// <summary>
    /// 随机前往一个广告
    /// </summary>
    /// <returns></returns>
    [HttpGet("/partner-random")]
    public async Task<ActionResult> RandomGo()
    {
        var ad = AdsService.GetByWeightedPrice((AdvertiseType)new Random().Next(1, 4), Request.Location());
        if (!Request.IsRobot() && string.IsNullOrEmpty(HttpContext.Session.Get<string>("ads" + ad.Id)))
        {
            HttpContext.Session.Set("ads" + ad.Id, ad.Id.ToString());
            ClickRecordService.AddEntity(new AdvertisementClickRecord()
            {
                IP = ClientIP.ToString(),
                Location = Request.Location(),
                Referer = Request.Headers[HeaderNames.Referer].ToString(),
                Time = DateTime.Now
            });
            await AdsService.SaveChangesAsync();
            var start = DateTime.Today.AddMonths(-1);
            await ClickRecordService.GetQuery(a => a.Time < start).ExecuteDeleteAsync();
        }

        return Redirect(ad.Url);
    }

    /// <summary>
    /// 广告访问记录
    /// </summary>
    /// <param name="id"></param>
    /// <param name="page"></param>
    /// <param name="size"></param>
    /// <returns></returns>
    [HttpGet("/partner/{id}/records"), MyAuthorize]
    public async Task<IActionResult> ClickRecords(int id, int page = 1, int size = 15, string kw = "")
    {
        Expression<Func<AdvertisementClickRecord, bool>> where = e => e.AdvertisementId == id;
        if (!string.IsNullOrEmpty(kw))
        {
            kw = Regex.Escape(kw);
            where = where.And(e => Regex.IsMatch(e.IP + e.Location + e.Referer, kw));
        }

        var pages = await ClickRecordService.GetQuery(where).OrderByDescending(e => e.Time).ProjectViewModel().ToPagedListNoLockAsync(page, size);
        return Ok(pages);
    }

    /// <summary>
    /// 导出广告访问记录
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [HttpGet("/partner/{id}/records-export"), MyAuthorize]
    public IActionResult ExportClickRecords(int id)
    {
        var list = ClickRecordService.GetQuery(e => e.AdvertisementId == id, e => e.Time, false).ProjectViewModel().ToPooledListScope();
        using var ms = list.ToExcel();
        var advertisement = AdsService[id];
        return this.ResumeFile(ms.ToArray(), ContentType.Xlsx, advertisement.Title + "访问记录.xlsx");
    }

    /// <summary>
    /// 广告访问记录图表
    /// </summary>
    /// <returns></returns>
    [HttpGet("/partner/{id}/records-chart"), MyAuthorize]
    [ProducesResponseType((int)HttpStatusCode.OK)]
    public async Task<IActionResult> ClickRecordsChart(int id, bool compare, uint period, CancellationToken cancellationToken)
    {
        if (compare)
        {
            var start1 = DateTime.Today.AddDays(-period);
            var list1 = await ClickRecordService.GetQuery(e => e.AdvertisementId == id && e.Time >= start1).Select(e => e.Time).GroupBy(t => t.Date).Select(g => new
            {
                Date = g.Key,
                Count = g.Count()
            }).OrderBy(a => a.Date).ToListWithNoLockAsync(cancellationToken);
            if (list1.Count == 0)
            {
                return Ok(Array.Empty<int>());
            }

            var start2 = start1.AddDays(-period - 1);
            var list2 = await ClickRecordService.GetQuery(e => e.AdvertisementId == id && e.Time >= start2 && e.Time < start1).Select(e => e.Time).GroupBy(t => t.Date).Select(g => new
            {
                Date = g.Key,
                Count = g.Count()
            }).OrderBy(a => a.Date).ToListWithNoLockAsync(cancellationToken);

            // 将数据填充成连续的数据
            for (var i = start1; i <= DateTime.Today; i = i.AddDays(1))
            {
                if (list1.All(a => a.Date != i))
                {
                    list1.Add(new { Date = i, Count = 0 });
                }
            }
            for (var i = start2; i < start1; i = i.AddDays(1))
            {
                if (list2.All(a => a.Date != i))
                {
                    list2.Add(new { Date = i, Count = 0 });
                }
            }
            return Ok(new[] { list1.OrderBy(a => a.Date), list2.OrderBy(a => a.Date) });
        }

        var list = await ClickRecordService.GetQuery(e => e.AdvertisementId == id).Select(e => e.Time).GroupBy(t => t.Date).Select(g => new
        {
            Date = g.Key,
            Count = g.Count()
        }).OrderBy(a => a.Date).ToListWithNoLockAsync(cancellationToken);
        var min = list.Min(a => a.Date);
        var max = list.Max(a => a.Date);
        for (var i = min; i < max; i = i.AddDays(1))
        {
            if (list.All(a => a.Date != i))
            {
                list.Add(new { Date = i, Count = 0 });
            }
        }

        return Ok(new[] { list.OrderBy(a => a.Date) });
    }

    /// <summary>
    /// 广告访问记录图表
    /// </summary>
    /// <returns></returns>
    [HttpGet("/partner/records-chart"), MyAuthorize]
    [ProducesResponseType((int)HttpStatusCode.OK)]
    public async Task<IActionResult> ClickRecordsChart(bool compare, uint period, CancellationToken cancellationToken)
    {
        if (compare)
        {
            var start1 = DateTime.Today.AddDays(-period);
            var list1 = await ClickRecordService.GetQuery(e => e.Time >= start1).Select(e => e.Time).GroupBy(t => t.Date).Select(g => new
            {
                Date = g.Key,
                Count = g.Count()
            }).OrderBy(a => a.Date).ToListWithNoLockAsync(cancellationToken);
            if (list1.Count == 0)
            {
                return Ok(Array.Empty<int>());
            }

            var start2 = start1.AddDays(-period - 1);
            var list2 = await ClickRecordService.GetQuery(e => e.Time >= start2 && e.Time < start1).Select(e => e.Time).GroupBy(t => t.Date).Select(g => new
            {
                Date = g.Key,
                Count = g.Count()
            }).OrderBy(a => a.Date).ToListWithNoLockAsync(cancellationToken);

            // 将数据填充成连续的数据
            for (var i = start1; i <= DateTime.Today; i = i.AddDays(1))
            {
                if (list1.All(a => a.Date != i))
                {
                    list1.Add(new { Date = i, Count = 0 });
                }
            }
            for (var i = start2; i < start1; i = i.AddDays(1))
            {
                if (list2.All(a => a.Date != i))
                {
                    list2.Add(new { Date = i, Count = 0 });
                }
            }
            return Ok(new[] { list1.OrderBy(a => a.Date), list2.OrderBy(a => a.Date) });
        }

        var start = DateTime.Now.AddMonths(-1);
        var list = await ClickRecordService.GetQuery(e => e.Time >= start).Select(e => e.Time).GroupBy(t => t.Date).Select(g => new
        {
            Date = g.Key,
            Count = g.Count()
        }).OrderBy(a => a.Date).ToListWithNoLockAsync(cancellationToken);
        var min = list.Min(a => a.Date);
        var max = list.Max(a => a.Date);
        for (var i = min; i < max; i = i.AddDays(1))
        {
            if (list.All(a => a.Date != i))
            {
                list.Add(new { Date = i, Count = 0 });
            }
        }

        return Ok(new[] { list.OrderBy(a => a.Date) });
    }

    /// <summary>
    /// 广告访问记录分析
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [HttpGet("/partner/{id}/insight"), MyAuthorize]
    public IActionResult ClickRecordsInsight(int id)
    {
        return View(AdsService[id].ToViewModel());
    }

    /// <summary>
    /// 设置分类
    /// </summary>
    /// <param name="id"></param>
    /// <param name="cids"></param>
    /// <returns></returns>
    /// <exception cref="NotFoundException"></exception>
    [HttpPost("/partner/{id}/categories"), DistributedLockFilter]
    public async Task<ActionResult> SetCategories(int id, [FromBodyOrDefault] string cids)
    {
        var entity = await AdsService.GetByIdAsync(id) ?? throw new NotFoundException("广告未找到");
        entity.CategoryIds = cids;
        await AdsService.SaveChangesAsync();
        return Ok();
    }
}