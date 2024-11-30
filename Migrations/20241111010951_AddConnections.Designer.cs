﻿// <auto-generated />
using System;
using KeyPulse.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace KeyPulse.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20241111010951_AddConnections")]
    partial class AddConnections
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "8.0.10")
                .HasAnnotation("Proxies:ChangeTracking", false)
                .HasAnnotation("Proxies:CheckEquality", false)
                .HasAnnotation("Proxies:LazyLoading", true);

            modelBuilder.Entity("KeyPulse.Models.Connection", b =>
                {
                    b.Property<int>("ConnectionID")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("ConnectedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("DeviceID")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<DateTime?>("DisconnectedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("ConnectionID");

                    b.HasIndex("DeviceID");

                    b.ToTable("Connections", (string)null);
                });

            modelBuilder.Entity("KeyPulse.Models.DeviceInfo", b =>
                {
                    b.Property<string>("DeviceID")
                        .HasColumnType("TEXT");

                    b.Property<string>("DeviceName")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<int>("DeviceType")
                        .HasColumnType("INTEGER");

                    b.Property<string>("PID")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("VID")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.HasKey("DeviceID");

                    b.ToTable("Devices", (string)null);
                });

            modelBuilder.Entity("KeyPulse.Models.Connection", b =>
                {
                    b.HasOne("KeyPulse.Models.DeviceInfo", "Device")
                        .WithMany("Connections")
                        .HasForeignKey("DeviceID")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Device");
                });

            modelBuilder.Entity("KeyPulse.Models.DeviceInfo", b =>
                {
                    b.Navigation("Connections");
                });
#pragma warning restore 612, 618
        }
    }
}